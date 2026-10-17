"""HexLive construction-stage authoring panel and FBX audit.

Interactive use (Blender 3.2+):

    exec(open("/Volumes/ORICO/HexLive/Tools/blender/construction_stage_authoring.py").read())

The panel appears in 3D Viewport -> N -> HexLive Build.  The artist keeps the
model root at identity, creates ordered stage empties, and moves logical units
under them.  One direct non-decoration child of a stage is one delivered
resource.  A unit may itself be an Empty containing several render meshes.

Headless audit:

    blender --background --factory-startup \
      --python Tools/blender/construction_stage_authoring.py -- \
      --audit-manifest Tools/construction_stage_manifest.json

Exports use export_construction_stage_workspace.py. Legacy generators recorded
in the manifest are provenance only and must not overwrite this edited source.
"""

import json
import importlib.util
import re
import sys
from pathlib import Path

import bpy


bl_info = {
    "name": "HexLive Construction Stages",
    "author": "HexLive",
    "version": (1, 0, 0),
    "blender": (3, 2, 0),
    "location": "3D Viewport > Sidebar > HexLive Build",
    "description": "Author and validate resource-by-resource construction stages",
    "category": "Object",
}


STAGE_RE = re.compile(r"^BuildStage_(\d+)(?:__(.+))?$")
LEGACY_STAGE_RE = re.compile(r"^(\d+)$")
HELPER_ROOT = "ConstructionHelpers"
ROLE_PROPERTY = "hexlive_construction_role"
MATERIAL_PROPERTY = "hexlive_material_id"
INDEX_PROPERTY = "hexlive_stage_index"
DEFINITION_PROPERTY = "hexlive_definition_id"

MATERIALS = (
    ("resource.log", "Бревно", "Большое бревно"),
    ("resource.stick", "Палка", "Палка или отдельная деревянная стойка"),
    ("resource.board", "Доска", "Одна строительная доска"),
    ("resource.rope", "Верёвка", "Одна доставленная верёвка"),
    ("resource.palm_leaf", "Лист", "Один пальмовый лист"),
    ("resource.stone", "Камень", "Один камень"),
)

MATERIAL_TOKEN = {
    "resource.log": "log",
    "resource.stick": "stick",
    "resource.board": "board",
    "resource.rope": "rope",
    "resource.palm_leaf": "palm_leaf",
    "resource.stone": "stone",
}
TOKEN_MATERIAL = {token: material for material, token in MATERIAL_TOKEN.items()}


def repository_root() -> Path:
    if bpy.data.filepath:
        for parent in Path(bpy.data.filepath).resolve().parents:
            if (parent / "Tools/construction_stage_manifest.json").exists():
                return parent
    configured = bpy.context.scene.get("hexlive_repository")
    if configured and (Path(configured) / "Tools/construction_stage_manifest.json").exists():
        return Path(configured)
    return Path(__file__).resolve().parents[2]


def manifest_path() -> Path:
    return repository_root() / "Tools" / "construction_stage_manifest.json"


def load_manifest(path: Path | None = None) -> dict:
    return json.loads((path or manifest_path()).read_text(encoding="utf-8"))


def definition_from_manifest(definition_id: str, manifest: dict | None = None) -> dict | None:
    for definition in (manifest or load_manifest()).get("definitions", []):
        if definition.get("id") == definition_id:
            return definition
    return None


def parse_stage_name(name: str) -> tuple[int, str | None] | None:
    name = re.sub(r"\.\d{3}$", "", name)
    match = STAGE_RE.match(name)
    if match:
        token = match.group(2)
        return int(match.group(1)), TOKEN_MATERIAL.get(token, token)
    match = LEGACY_STAGE_RE.match(name)
    if match:
        return int(match.group(1)), None
    return None


def stage_name(index: int, material: str) -> str:
    return f"BuildStage_{index}__{MATERIAL_TOKEN[material]}"


def configure_stage(stage, index: int, material: str) -> None:
    """Apply the canonical metadata without touching the stage transform."""
    stage.name = stage_name(index, material)
    stage[INDEX_PROPERTY] = index
    stage[MATERIAL_PROPERTY] = material
    stage.empty_display_type = "PLAIN_AXES"
    stage.empty_display_size = 0.12


def material_for_stage(stage, definition: dict | None = None) -> str | None:
    explicit = stage.get(MATERIAL_PROPERTY)
    if explicit:
        return str(explicit)
    parsed = parse_stage_name(stage.name)
    if parsed and parsed[1]:
        return parsed[1]
    if definition and parsed:
        for expected in definition.get("stages", []):
            if int(expected["index"]) == parsed[0]:
                return expected["material"]
    return None


def stages_below(root) -> list:
    found = []
    stack = list(root.children) if root is not None else list(bpy.context.scene.objects)
    seen = set()
    while stack:
        node = stack.pop()
        if node in seen:
            continue
        seen.add(node)
        parsed = parse_stage_name(node.name)
        if parsed:
            found.append((parsed[0], node))
            continue
        stack.extend(node.children)
    found.sort(key=lambda pair: pair[0])
    return found


def active_authoring_root(context):
    node = context.active_object
    if node is None:
        return None
    owner = node
    while owner is not None:
        if owner.get(DEFINITION_PROPERTY):
            return owner
        owner = owner.parent
    while node.parent is not None:
        if node.get(DEFINITION_PROPERTY) or stages_below(node):
            break
        node = node.parent
    if node.get(DEFINITION_PROPERTY) or stages_below(node):
        return node
    return context.active_object


def preserve_world_parent(obj, parent) -> None:
    world = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_world = world


def has_render_mesh(node) -> bool:
    if node.type == "MESH":
        return True
    return any(child.type == "MESH" for child in node.children_recursive)


def is_free_child(node) -> bool:
    return (
        node.get(ROLE_PROPERTY) in {"helper", "decoration"}
        or "_deco_" in node.name
        or node.name.startswith("HL_Door_State_")
    )


def resource_units(stage) -> list:
    units = []
    for child in stage.children:
        if is_free_child(child):
            continue
        if child.name == HELPER_ROOT:
            continue
        if child.name.startswith("HL_Door_Pivot"):
            units.extend(nested for nested in child.children if not is_free_child(nested))
            continue
        units.append(child)
    return sorted(units, key=lambda obj: str(obj.get("hexlive_export_name", obj.name)))


def unit_sequence(root, definition=None):
    """The same sorted stage/unit order consumed by the exported FBX readers."""
    sequence = []
    for index, stage in stages_below(root):
        material = material_for_stage(stage, definition)
        for unit in resource_units(stage):
            sequence.append((index, material, unit))
    return sequence


def show_units(root, count):
    sequence = unit_sequence(root)
    count = max(0, min(count, len(sequence)))
    for _, stage in stages_below(root):
        stage.hide_set(False)
        stage.hide_render = False
    for index, (_, _, unit) in enumerate(sequence):
        for obj in [unit, *unit.children_recursive]:
            obj.hide_set(index >= count)
            obj.hide_render = index >= count
    root["hexlive_preview_units"] = count
    return count, len(sequence)


def top_selected(context, forbidden: set) -> list:
    selected = [obj for obj in context.selected_objects if obj not in forbidden]
    selected_set = set(selected)
    return [obj for obj in selected if obj.parent not in selected_set]


def next_unit_name(stage, material: str) -> str:
    prefix = MATERIAL_TOKEN[material]
    used = {child.name for child in stage.children}
    index = 0
    while f"{prefix}_unit_{index:02d}" in used:
        index += 1
    return f"{prefix}_unit_{index:02d}"


def validate_root(root, definition: dict | None = None) -> list[str]:
    issues = []
    stages = stages_below(root)
    if not stages:
        return [f"{root.name}: нет BuildStage_* (или legacy 1/2/3) родителей"]
    indices = [index for index, _ in stages]
    if len(indices) != len(set(indices)):
        issues.append(f"{root.name}: повторяется номер стадии")
    if indices != list(range(1, max(indices) + 1)):
        issues.append(f"{root.name}: стадии должны идти подряд с 1, сейчас {indices}")

    expected_by_index = {
        int(stage["index"]): stage for stage in (definition or {}).get("stages", [])
    }
    for index, stage in stages:
        units = resource_units(stage)
        for unit in units:
            if not has_render_mesh(unit):
                issues.append(
                    f"{root.name}/{stage.name}/{unit.name}: unit не содержит render mesh"
                )
        material = material_for_stage(stage, definition)
        expected = expected_by_index.get(index)
        if material is None:
            issues.append(f"{root.name}/{stage.name}: не задан material")
        if expected:
            if material and material != expected["material"]:
                issues.append(
                    f"{root.name}/{stage.name}: material {material}, ожидался "
                    f"{expected['material']}"
                )
            if len(units) != int(expected["units"]):
                issues.append(
                    f"{root.name}/{stage.name}: {len(units)} units, ожидалось "
                    f"{expected['units']}"
                )
    if expected_by_index and set(indices) != set(expected_by_index):
        issues.append(
            f"{root.name}: стадии {indices}, manifest ждёт {sorted(expected_by_index)}"
        )
    return issues


def import_fbx(path: Path) -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=str(path))


def audit_definition(definition: dict, repo: Path) -> tuple[list[str], list[str]]:
    errors, warnings = [], []
    export = repo / definition["export"]
    if not export.exists():
        return [f"{definition['id']}: нет {definition['export']}"], warnings
    import_fbx(export)
    virtual_root = bpy.data.objects.new("__audit_root__", None)
    roots = [obj for obj in bpy.context.scene.objects if obj.parent is None]
    for root in roots:
        if root != virtual_root:
            root.parent = virtual_root
    issues = validate_root(virtual_root, definition)
    status = definition.get("status", "ready")
    if status == "ready":
        errors.extend(f"{definition['id']}: {issue}" for issue in issues)
    else:
        warnings.extend(f"{definition['id']} [{status}]: {issue}" for issue in issues)
    return errors, warnings


def audit_manifest(path: Path) -> int:
    manifest = load_manifest(path)
    repo = path.resolve().parents[1]
    errors, warnings = [], []
    for definition in manifest.get("definitions", []):
        current_errors, current_warnings = audit_definition(definition, repo)
        errors.extend(current_errors)
        warnings.extend(current_warnings)
    for warning in warnings:
        print("STAGE_WARNING", warning)
    for error in errors:
        print("STAGE_ERROR", error)
    print(
        "STAGE_AUDIT",
        f"definitions={len(manifest.get('definitions', []))}",
        f"warnings={len(warnings)}",
        f"errors={len(errors)}",
    )
    return 1 if errors else 0


class HEXLIVE_PG_stage_settings(bpy.types.PropertyGroup):
    definition_id: bpy.props.StringProperty(name="Definition id")
    stage_index: bpy.props.IntProperty(name="Стадия", default=1, min=1, max=32)
    material: bpy.props.EnumProperty(name="Материал", items=MATERIALS)


class HEXLIVE_OT_prepare_stage_root(bpy.types.Operator):
    bl_idname = "hexlive.prepare_stage_root"
    bl_label = "Подготовить parent"
    bl_description = "Пометить выбранный объект как корень поэтапной постройки"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        root = context.active_object
        settings = context.scene.hexlive_stage_settings
        if root is None:
            self.report({"ERROR"}, "Сначала выберите parent object")
            return {"CANCELLED"}
        definition_id = settings.definition_id.strip()
        if not definition_id:
            self.report({"ERROR"}, "Укажите definition id")
            return {"CANCELLED"}
        root[DEFINITION_PROPERTY] = definition_id
        self.report({"INFO"}, f"Parent: {definition_id}")
        return {"FINISHED"}


class HEXLIVE_OT_add_stage(bpy.types.Operator):
    bl_idname = "hexlive.add_stage"
    bl_label = "Создать стадию"
    bl_description = "Создать BuildStage_N под активным parent"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        root = active_authoring_root(context)
        settings = context.scene.hexlive_stage_settings
        if root is None:
            self.report({"ERROR"}, "Не найден parent object")
            return {"CANCELLED"}
        existing = dict(stages_below(root))
        if settings.stage_index in existing:
            self.report({"ERROR"}, f"Стадия {settings.stage_index} уже существует")
            return {"CANCELLED"}
        stage = bpy.data.objects.new(
            stage_name(settings.stage_index, settings.material), None
        )
        root.users_collection[0].objects.link(stage)
        stage.parent = root
        configure_stage(stage, settings.stage_index, settings.material)
        context.view_layer.objects.active = stage
        stage.select_set(True)
        return {"FINISHED"}


class HEXLIVE_OT_prepare_manifest_stages(bpy.types.Operator):
    bl_idname = "hexlive.prepare_manifest_stages"
    bl_label = "Создать/обновить все стадии"
    bl_description = (
        "Создать стадии из manifest; legacy 1/2/3 будут переименованы, "
        "существующие children останутся на месте"
    )
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        root = active_authoring_root(context)
        settings = context.scene.hexlive_stage_settings
        if root is None:
            self.report({"ERROR"}, "Сначала выберите parent object")
            return {"CANCELLED"}

        definition_id = str(root.get(DEFINITION_PROPERTY, "")).strip()
        if not definition_id:
            definition_id = settings.definition_id.strip()
            if definition_id:
                root[DEFINITION_PROPERTY] = definition_id
        definition = definition_from_manifest(definition_id) if definition_id else None
        if definition is None:
            self.report({"ERROR"}, "Definition id отсутствует в manifest")
            return {"CANCELLED"}

        existing = dict(stages_below(root))
        collection = root.users_collection[0] if root.users_collection else context.collection
        created = 0
        for expected in definition.get("stages", []):
            index = int(expected["index"])
            material = str(expected["material"])
            stage = existing.get(index)
            if stage is None:
                stage = bpy.data.objects.new(stage_name(index, material), None)
                collection.objects.link(stage)
                stage.parent = root
                created += 1
            configure_stage(stage, index, material)

        settings.definition_id = definition_id
        self.report(
            {"INFO"},
            f"Стадий: {len(definition.get('stages', []))}; создано: {created}",
        )
        return {"FINISHED"}


class HEXLIVE_OT_assign_stage(bpy.types.Operator):
    bl_idname = "hexlive.assign_stage"
    bl_label = "Перенести выбранное в стадию"
    bl_options = {"REGISTER", "UNDO"}
    stage_index: bpy.props.IntProperty()

    def execute(self, context):
        root = active_authoring_root(context)
        stages = dict(stages_below(root)) if root else {}
        stage = stages.get(self.stage_index)
        if root is None or stage is None:
            self.report({"ERROR"}, "Стадия не найдена")
            return {"CANCELLED"}
        forbidden = {root, *stages.values()}
        selected = top_selected(context, forbidden)
        if not selected:
            self.report({"ERROR"}, "Выберите mesh или logical unit")
            return {"CANCELLED"}
        for obj in selected:
            preserve_world_parent(obj, stage)
            obj[ROLE_PROPERTY] = "unit"
        self.report({"INFO"}, f"В стадию {self.stage_index}: {len(selected)} unit(s)")
        return {"FINISHED"}


class HEXLIVE_OT_wrap_stage_unit(bpy.types.Operator):
    bl_idname = "hexlive.wrap_stage_unit"
    bl_label = "Объединить в один resource unit"
    bl_description = "Несколько выбранных meshes будут включаться за одну доставку"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        root = active_authoring_root(context)
        settings = context.scene.hexlive_stage_settings
        stages = dict(stages_below(root)) if root else {}
        stage = stages.get(settings.stage_index)
        if root is None or stage is None:
            self.report({"ERROR"}, "Сначала создайте выбранную стадию")
            return {"CANCELLED"}
        selected = top_selected(context, {root, *stages.values()})
        if not selected:
            self.report({"ERROR"}, "Выберите части одного ресурса")
            return {"CANCELLED"}
        unit = bpy.data.objects.new(next_unit_name(stage, settings.material), None)
        root.users_collection[0].objects.link(unit)
        unit.parent = stage
        unit[ROLE_PROPERTY] = "unit"
        unit[MATERIAL_PROPERTY] = settings.material
        for obj in selected:
            preserve_world_parent(obj, unit)
        context.view_layer.objects.active = unit
        unit.select_set(True)
        self.report({"INFO"}, f"Один resource unit из {len(selected)} частей")
        return {"FINISHED"}


class HEXLIVE_OT_mark_stage_helper(bpy.types.Operator):
    bl_idname = "hexlive.mark_stage_helper"
    bl_label = "Пометить как helper/deco"
    bl_description = "Сокет, маркер или бесплатный декор не считается ресурсом"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        root = active_authoring_root(context)
        if root is None:
            return {"CANCELLED"}
        helper = next((child for child in root.children if child.name == HELPER_ROOT), None)
        if helper is None:
            helper = bpy.data.objects.new(HELPER_ROOT, None)
            root.users_collection[0].objects.link(helper)
            helper.parent = root
            helper[ROLE_PROPERTY] = "helper"
        selected = top_selected(context, {root, helper})
        for obj in selected:
            preserve_world_parent(obj, helper)
            obj[ROLE_PROPERTY] = "helper"
        self.report({"INFO"}, f"Helpers: {len(selected)}")
        return {"FINISHED"}


class HEXLIVE_OT_preview_stages(bpy.types.Operator):
    bl_idname = "hexlive.preview_stages"
    bl_label = "Показать до стадии"
    bl_options = {"REGISTER"}
    through: bpy.props.IntProperty(default=999)

    def execute(self, context):
        root = active_authoring_root(context)
        if root is None:
            return {"CANCELLED"}
        # Keep the controls usable when preview hides the selected mesh.
        context.view_layer.objects.active = root
        root.select_set(True)
        for index, stage in stages_below(root):
            show = index <= self.through
            for obj in [stage, *stage.children_recursive]:
                obj.hide_set(not show)
                obj.hide_render = not show
        root["hexlive_preview_units"] = sum(
            len(resource_units(stage)) for index, stage in stages_below(root)
            if index <= self.through)
        return {"FINISHED"}


class HEXLIVE_OT_preview_piece(bpy.types.Operator):
    bl_idname = "hexlive.preview_piece"
    bl_label = "Одно строительное действие"
    delta: bpy.props.IntProperty(default=1)
    reset: bpy.props.BoolProperty(default=False)

    def execute(self, context):
        root = active_authoring_root(context)
        if root is None:
            return {"CANCELLED"}
        context.view_layer.objects.active = root
        root.select_set(True)
        sequence = unit_sequence(root)
        current = int(root.get("hexlive_preview_units", len(sequence)))
        show_units(root, 0 if self.reset else current+self.delta)
        return {"FINISHED"}


class HEXLIVE_OT_validate_stage_root(bpy.types.Operator):
    bl_idname = "hexlive.validate_stage_root"
    bl_label = "Проверить стадии"
    bl_options = {"REGISTER"}

    def execute(self, context):
        root = active_authoring_root(context)
        if root is None:
            return {"CANCELLED"}
        definition_id = str(root.get(DEFINITION_PROPERTY, ""))
        definition = definition_from_manifest(definition_id) if definition_id else None
        issues = validate_root(root, definition)
        if issues:
            for issue in issues:
                print("STAGE_ERROR", issue)
            self.report({"ERROR"}, f"Ошибок: {len(issues)}; детали в System Console")
            return {"CANCELLED"}
        self.report({"INFO"}, "Стадии и число resource units совпадают с manifest")
        return {"FINISHED"}


class HEXLIVE_OT_export_stage_root(bpy.types.Operator):
    bl_idname = "hexlive.export_stage_root"
    bl_label = "Экспорт FBX этого объекта"
    bl_description = "Экспортировать раскладку из общего .blend в runtime FBX"

    def execute(self, context):
        root = active_authoring_root(context)
        if root is None:
            return {"CANCELLED"}
        definition = definition_from_manifest(str(root.get(DEFINITION_PROPERTY, "")))
        if definition is None:
            self.report({"ERROR"}, "Неизвестный definition id")
            return {"CANCELLED"}
        path = repository_root() / "Tools/blender/export_construction_stage_workspace.py"
        spec = importlib.util.spec_from_file_location("hexlive_stage_export", path)
        module = importlib.util.module_from_spec(spec)
        try:
            spec.loader.exec_module(module)
            output = module.export_definition(definition)
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        self.report({"INFO"}, "FBX: " + str(output))
        return {"FINISHED"}


class HEXLIVE_PT_construction_stages(bpy.types.Panel):
    bl_label = "HexLive Construction Stages"
    bl_idname = "HEXLIVE_PT_construction_stages"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "HexLive Build"

    def draw(self, context):
        layout = self.layout
        settings = context.scene.hexlive_stage_settings
        root = active_authoring_root(context)
        box = layout.box()
        box.label(text="1. Parent object")
        box.prop(settings, "definition_id")
        box.operator("hexlive.prepare_stage_root")
        box.operator("hexlive.prepare_manifest_stages", icon="OUTLINER_OB_EMPTY")
        if root is None:
            layout.label(text="Выберите root модели", icon="INFO")
            return

        definition_id = str(root.get(DEFINITION_PROPERTY, ""))
        layout.label(text=f"Root: {root.name}")
        if definition_id:
            layout.label(text=definition_id, icon="FILE_TICK")
        box = layout.box()
        box.label(text="2. Новая стадия")
        box.prop(settings, "stage_index")
        box.prop(settings, "material")
        box.operator("hexlive.add_stage")

        box = layout.box()
        box.label(text="3. Раскладка children")
        definition = definition_from_manifest(definition_id) if definition_id else None
        for index, stage in stages_below(root):
            row = box.row(align=True)
            material = material_for_stage(stage, definition) or "?"
            row.label(text=f"{index}: {material} × {len(resource_units(stage))}")
            op = row.operator("hexlive.assign_stage", text="Назначить")
            op.stage_index = index
        box.operator("hexlive.wrap_stage_unit", icon="OUTLINER_OB_EMPTY")
        box.operator("hexlive.mark_stage_helper", icon="EMPTY_AXIS")

        box = layout.box()
        box.label(text="4. Preview / проверка")
        row = box.row(align=True)
        for index, _ in stages_below(root):
            op = row.operator("hexlive.preview_stages", text=str(index))
            op.through = index
        op = row.operator("hexlive.preview_stages", text="Все")
        op.through = 999
        box.operator("hexlive.validate_stage_root", icon="CHECKMARK")
        sequence = unit_sequence(root, definition)
        current = int(root.get("hexlive_preview_units", len(sequence)))
        box.label(text=f"По одной детали: {current} / {len(sequence)}")
        row = box.row(align=True)
        first = row.operator("hexlive.preview_piece", text="0")
        first.reset = True
        previous = row.operator("hexlive.preview_piece", text="−1 деталь")
        previous.delta = -1
        row.operator("hexlive.preview_piece", text="+1 деталь")
        if bpy.data.filepath and Path(bpy.data.filepath).name == "hexlive_construction_stages.blend":
            box.operator("hexlive.export_stage_root", icon="EXPORT")


CLASSES = (
    HEXLIVE_PG_stage_settings,
    HEXLIVE_OT_prepare_stage_root,
    HEXLIVE_OT_prepare_manifest_stages,
    HEXLIVE_OT_add_stage,
    HEXLIVE_OT_assign_stage,
    HEXLIVE_OT_wrap_stage_unit,
    HEXLIVE_OT_mark_stage_helper,
    HEXLIVE_OT_preview_stages,
    HEXLIVE_OT_preview_piece,
    HEXLIVE_OT_validate_stage_root,
    HEXLIVE_OT_export_stage_root,
    HEXLIVE_PT_construction_stages,
)


def unregister() -> None:
    if hasattr(bpy.types.Scene, "hexlive_stage_settings"):
        del bpy.types.Scene.hexlive_stage_settings
    for cls in reversed(CLASSES):
        try:
            existing = getattr(bpy.types, cls.__name__, cls)
            bpy.utils.unregister_class(existing)
        except (RuntimeError, ValueError):
            pass


def register() -> None:
    unregister()
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.hexlive_stage_settings = bpy.props.PointerProperty(
        type=HEXLIVE_PG_stage_settings
    )


def main() -> None:
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if "--audit-manifest" in args:
        index = args.index("--audit-manifest")
        path = Path(args[index + 1]).resolve()
        raise SystemExit(audit_manifest(path))
    register()
    print("HEXLIVE_STAGE_AUTHORING registered: 3D Viewport > N > HexLive Build")


if __name__ == "__main__":
    main()
