# Исследование: карта шероховатости (smoothness map) в Unity URP

Handoff для агента, работающего над per-pixel блеском (кейс: рана/влажный участок
блестит, остальная поверхность матовая). Это результат интернет-исследования
готовых решений; код проекта в рамках этого исследования НЕ изучался — сверь
выводы с нашим фактическим шейдером кожи перед применением (см. также памятки
`hub_skin_paint`, `project_dead_gloss_map_variant`: в проекте уже есть история
с мёртвым вариантом карты блеска и пином «винил»).

## TL;DR

В Unity нет «roughness map» как отдельного слота — есть **smoothness**
(инверсия roughness), и она **уже умеет быть картой** тремя штатными способами:

1. **URP/Lit без кода:** альфа-канал Metallic Map = per-pixel карта глянца.
2. **Shader Graph:** grayscale-маска → вход Smoothness мастер-ноды Lit.
3. **Рукописный HLSL:** `surfaceData.smoothness = mask * _Smoothness;` перед
   `UniversalFragmentPBR(inputData, surfaceData)`.

Для «сфера, где пятно блестит, остальное матовое» достаточно варианта 1:
одна RGBA-текстура (RGB — metallic, можно чёрный; A — маска: белое = глянец),
в слот Metallic Map, слайдер Smoothness = 1.

## 1. Штатный URP/Lit (metallic alpha)

- Smoothness-слайдер в Lit — это МНОЖИТЕЛЬ поверх карты, если карта назначена.
- Источник карты переключается в инспекторе (Source): альфа Metallic Map
  (default) или альфа Base Map. Ключевые слова шейдера:
  `_METALLICSPECGLOSSMAP`, `_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A`.
- Это документированный channel-packing URP: metallic в RGB, smoothness в A.
  https://docs.unity3d.com/6000.4/Documentation/Manual/urp/lit-shader.html

⚠️ **Ловушка дефолтной текстуры.** Без назначенной metallic-карты Unity
подставляет «black» с альфой 0; тупое умножение `map.a * _Smoothness` глушит
блеск в ноль на всех материалах без карты. URP Lit решает это УСЛОВНО через
keyword: карта есть → `a * _Smoothness`, карты нет → чистый слайдер. В своём
шейдере это ветвление обязательно повторить.
https://discussions.unity.com/t/mimicking-the-urp-lit-metallic-alpha-channel-to-gloss-map-effect/933114

## 2. Shader Graph

Схема из всех «wet surface»-туториалов одинаковая:

```
Sample Texture 2D (маска, grayscale) → Multiply(_WetStrength) → Smoothness (Lit master)
```

Чтобы блеск читался как «влажное», а не «пластик», в той же маске делают ещё два
эффекта (разбор: https://www.shaderic.com/tutorials/RealisticMaterialWetness.html
и https://medium.com/@tayhaocheng.media/creating-a-puddles-shader-in-unity-from-observation-to-implementation-828f85dc2a4e):

- **Затемнение albedo** в мокрой зоне: `lerp(albedo, albedo*albedo, mask)` —
  квадрат, а не умножение на константу, чтобы не пережигать светлое.
- **Сплющивание нормали** к (0.5, 0.5, 1) при сильной «мокрости» — вода
  заливает микрорельеф.
- В shaderic-туториале маска = один grayscale-канал, который художник рисует, +
  один float «сила», управляемый геймплеем. Порождают маску из инвертированной
  height map с поднятым контрастом.

Для раны это ровно наш рецепт: одна маска раны → smoothness вверх, albedo
темнее, normal тише.

## 3. Рукописный HLSL-шейдер URP

Канонический шаблон: https://github.com/Cyanilux/URP_ShaderCodeTemplates/blob/main/URP_PBRLitTemplate.shader
(туториал: https://www.cyanilux.com/tutorials/urp-shader-code/).

Суть: «своя карта шероховатости» — это НЕ своя модель освещения. Заполняешь
поле в `SurfaceData` и отдаёшь стандартной функции, весь PBR-отклик (блик GGX,
отражения, пробы) URP считает сам:

```hlsl
half4 mg = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv);
surfaceData.metallic   = mg.r * _Metallic;
#ifdef _METALLICSPECGLOSSMAP
    surfaceData.smoothness = mg.a * _Smoothness;   // карта есть — альфа рулит
#else
    surfaceData.smoothness = _Smoothness;          // карты нет — слайдер
#endif
...
half4 color = UniversalFragmentPBR(inputData, surfaceData);
```

Свойства в шаблоне Cyanilux (для ориентира):

```
[Toggle(_SPECULAR_SETUP)] _MetallicSpecToggle (...)
[Toggle(_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A)] _SmoothnessSource (...)
_Metallic("Metallic", Range(0,1)) = 0
_Smoothness("Smoothness", Range(0,1)) = 0.5
_MetallicSpecGlossMap("Specular or Metallic Map", 2D) = "black" {}
```

Эталон того, как это делает сам Unity: `LitInput.hlsl` →
`SampleMetallicSpecGloss()` в пакете URP.

## Нюансы, которые везде всплывают

- **Roughness ≠ Smoothness.** Unity живёт в smoothness; карту roughness из
  Substance/внешних PBR-паков подключать через One Minus / `1.0 - r`.
  https://community.gamedev.tv/t/roughness-map-in-the-unity-urp-built-in-standard-shaders/214425
- **Импорт маски: sRGB OFF.** Маска — данные, не цвет; с включённым sRGB
  значения гнутся гаммой и середина маски «уезжает».
- **Блеску нужно что отражать.** Directional light со specular и/или
  reflection probe / skybox. На пустой тёмной сцене идеально гладкая сфера всё
  равно выглядит тускло — это не баг шейдера.
- **Альфа-канал должен пережить импорт.** Формат сжатия текстуры обязан нести
  альфу (например, не BC1); проверь в импортёре, что Alpha Source = Input
  Texture Alpha.

## План минимального стенда «сфера с блестящим пятном»

1. Сгенерировать RGBA PNG: RGB = чёрный, A = белое пятно на чёрном (это и есть
   маска глянца). sRGB off, Alpha = from input.
2. Материал URP/Lit: Metallic Map = эта текстура, Smoothness = 1,
   Source = Metallic Alpha.
3. Сфера + directional light + reflection probe (или skybox по умолчанию).
4. Ожидание: пятно даёт чёткий блик и отражения, остальная сфера матовая.
   Если блестит всё/ничего — смотри ловушки выше (sRGB, альфа, keyword).

## Источники

- https://docs.unity3d.com/6000.4/Documentation/Manual/urp/lit-shader.html
- https://discussions.unity.com/t/mimicking-the-urp-lit-metallic-alpha-channel-to-gloss-map-effect/933114
- https://www.shaderic.com/tutorials/RealisticMaterialWetness.html
- https://medium.com/@tayhaocheng.media/creating-a-puddles-shader-in-unity-from-observation-to-implementation-828f85dc2a4e
- https://github.com/Cyanilux/URP_ShaderCodeTemplates/blob/main/URP_PBRLitTemplate.shader
- https://www.cyanilux.com/tutorials/urp-shader-code/
- https://community.gamedev.tv/t/roughness-map-in-the-unity-urp-built-in-standard-shaders/214425
