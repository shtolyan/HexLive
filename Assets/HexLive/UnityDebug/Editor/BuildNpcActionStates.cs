using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// One-click builder for the clip-based action states on the NPC animator
    /// (Talk / Gather / Drink / Attack / Death). Idempotent — safe to re-run.
    /// Menu: <b>HexLive ▸ Build NPC Action States</b>.
    ///
    /// The base clips set here are the KEYS an AnimatorOverrideController swaps
    /// at runtime (random talk/death, weapon-specific attack) — see NpcActorView.
    /// </summary>
    public static class BuildNpcActionStates
    {
        const string ControllerPath =
            "Assets/HexLive/UnityPresentation/Actors/HexNpcLocomotion.controller";
        const string AnimDir = "Assets/ImportedActors/AnimLibrary/";

        [MenuItem("HexLive/Build NPC Action States")]
        public static void Build()
        {
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (ac == null)
            {
                Debug.LogError($"[NpcActionStates] Controller not found at {ControllerPath}");
                return;
            }

            AddParam(ac, "Talking", AnimatorControllerParameterType.Bool);
            AddParam(ac, "Gathering", AnimatorControllerParameterType.Bool);
            AddParam(ac, "Drinking", AnimatorControllerParameterType.Bool);
            // §Wardrobe-anim: dress = don beat (after gather), undress = doff beat
            // (before gather). Base clips are overridden at runtime by NpcAnimSet.
            AddParam(ac, "Dressing", AnimatorControllerParameterType.Bool);
            AddParam(ac, "Undressing", AnimatorControllerParameterType.Bool);
            AddParam(ac, "Dead", AnimatorControllerParameterType.Bool);
            AddParam(ac, "Attack", AnimatorControllerParameterType.Trigger);
            // §29C.3-hit: a standing stagger when damage lands while she stands still.
            AddParam(ac, "HitReact", AnimatorControllerParameterType.Trigger);
            AddParam(ac, "Crawling", AnimatorControllerParameterType.Bool); // §50: lost a leg
            // §105: тело РУХНУЛО — умирает или лежит без сознания. Свой bool, а
            // не переиспользованный Laying: сон входит в LieDown из каждого
            // стоячего состояния своим переходом и уходит в кровать, а падение
            // приходит откуда угодно, включая бег и драку.
            AddParam(ac, "Fallen", AnimatorControllerParameterType.Bool);
            AddParam(ac, "Chopping", AnimatorControllerParameterType.Bool); // §axe: swinging an axe at work
            // §gear-craft v2: the staged in-place craft — kneeling over the
            // laid-out ingredients, working the ground (planting-style clip).
            AddParam(ac, "Crafting", AnimatorControllerParameterType.Bool);
            // §71: gait blend — 0 walk, 0.5 slow run, 1 run. Separate from
            // "Speed" (which stays the Idle<->Walk switch) so the two never
            // fight: Speed decides WHETHER she moves, Gait decides HOW.
            AddParam(ac, "Gait", AnimatorControllerParameterType.Float);
            // §81: одна ОДНОРАЗОВАЯ сценка на все случаи — безделье и такты
            // сцены абьюза. Состояние одно, а клип в него вид заряжает перед
            // самым срабатыванием через AnimatorOverrideController: плясать,
            // приседать, шарить по карманам, отшатнуться, плакать — это всё
            // одна и та же машина «сыграй раз и вернись в Idle», и разводить её
            // на пять состояний значило бы пять раз написать одни и те же
            // переходы.
            AddParam(ac, "Emote", AnimatorControllerParameterType.Trigger);
            // §77.5: playback speed of the one-gesture work states, so ONE
            // interaction is ONE playthrough of its clip however long the sim
            // made the window. Same idiom as JumpSpeed (§HexHop). Default 1 =
            // authored pace, so a controller built before this reads identical.
            AddParam(ac, "ActionSpeed", AnimatorControllerParameterType.Float, 1f);
            // §104 r4: то же для УДАРА, но своим параметром — иначе боевой темп
            // и рабочий писали бы в одну ячейку по очереди каждый кадр. Клип
            // замаха обязан уложиться ровно в окно, которое открыл сим: у
            // кулака Punch A длится 2.17 с против окна 1.5 с, и без подгонки
            // удар либо обрывается на выходе в Idle, либо доигрывает поверх
            // следующего замаха.
            AddParam(ac, "AttackSpeed", AnimatorControllerParameterType.Float, 1f);

            var sm = ac.layers[0].stateMachine;
            var idle = Find(sm, "Idle");
            // §71: the Walk state becomes a 3-way gait blend (walk/slow run/run).
            BuildGaitBlend(ac, sm);

            var talk = AddState(sm, "Talk", Clip("X Bot@Talking"));
            var gather = AddState(sm, "Gather", Clip("X Bot@Gathering Objects"));
            var drink = AddState(sm, "Drink", Clip("X Bot@Drinking"));
            // §Wardrobe-anim: both wardrobe beats currently share one placeholder
            // take ("X Bot@Dressing" = Hostage Situation Idle). Swap to a real
            // don/doff clip by assigning NpcAnimSet.dress / .undress, or repoint
            // these base clips at dedicated takes.
            var dress = AddState(sm, "Dress", Clip("X Bot@Dressing"));
            var undress = AddState(sm, "Undress", Clip("X Bot@Dressing"));
            var attack = AddState(sm, "Attack", Clip("X Bot@Bayonet Stab"));
            var death = AddState(sm, "Death", Clip("X Bot@Death From Back Headshot"));
            // §29C.3-hit: the one-shot damage stagger (fired only when standing).
            var hitReact = AddState(sm, "HitReact", Clip("X Bot@Standing React Large From Right_once"));
            // §axe: chopping/mining with an axe or pickaxe (Harvest/Process) plays
            // a real looping swing clip instead of the old procedural shoulder pose.
            var chop = AddState(sm, "Chop", Clip("Standing Melee Attack Horizontal"));
            // §gear-craft v2: crafting kneels her over the ingredients laid out
            // on the ground — the Mixamo planting loop reads as assembling them.
            var craft = AddState(sm, "CraftWork", Clip("X Bot@Plant A Plant"));
            // §50: lost a leg → crawl. A 1D blend on Speed: at rest she lies in
            // a prone idle, moving she crawls (Zombie Crawl). Replaces the whole
            // stand/walk locomotion while Crawling; sim crawls her at 1/3 speed.
            var crawl = BuildCrawlBlend(ac, sm);
            // Базовый клип — просто КЛЮЧ для подмены; какой именно, роли не
            // играет, лишь бы он существовал.
            var emote = AddState(sm, "Emote", Clip("X Bot@Salsa Dancing"));
            // §105: рухнула → лежит → встала. Три состояния на один bool, ровно
            // как LieDown→Sleep→GetUp у сна, но своей цепочкой: сон уходит в
            // кровать и держит свою позу, а это падение с любого места.
            var fallDown = AddState(sm, "FallDown", Clip("X Bot@Falling Down_once"));
            var fallenIdle = AddState(sm, "FallenIdle", Clip("X Bot@Sleeping Idle"));
            var standUp = AddState(sm, "StandUp", Clip("X Bot@Standing Up_once"));
            // §105: беспамятство — ОДИН КАДР позы сна, намертво. Скорость 0
            // означает «оставайся на первом кадре»: тело лежит, но не дышит и
            // не ворочается — тем и отличается от спящей, которая рядом играет
            // ТОТ ЖЕ клип живьём. Клип, а не отдельная поза, потому что вторая
            // копия той же позы разошлась бы с первой при первой же замене.
            fallenIdle.speed = 0f;

            // Loopy activities: enter while the bool is set, return to Idle when cleared.
            Loopy(sm, talk, idle, "Talking");
            Loopy(sm, gather, idle, "Gathering");
            Loopy(sm, drink, idle, "Drinking");
            Loopy(sm, dress, idle, "Dressing");
            Loopy(sm, undress, idle, "Undressing");
            // §axe: enter Chop while the Chopping bool is set (axe/pickaxe work),
            // loop the swing, return to Idle when it clears.
            Loopy(sm, chop, idle, "Chopping");
            Loopy(sm, craft, idle, "Crafting");

            // §77.5: the two one-gesture work states play their clip exactly
            // once per interaction (see FitClipToWindow).
            FitClipToWindow(gather);
            FitClipToWindow(craft);

            // Attack: fired by a trigger, plays once, exits by time.
            //
            // §104 r4, два изменения, оба про рассинхрон с симуляцией:
            //
            // speedParameter — клип играет ровно столько, сколько окно замаха
            // в модели (вид считает AttackSpeed из живой длины клипа). Без него
            // авторская длина и sim-окно были ДВА ЧИСЛА, ОБЯЗАННЫЕ СОВПАДАТЬ, и
            // не совпадали ни у одного оружия: кулак 2.17 против 1.5, копьё
            // 3.27 против 2.0.
            //
            // canTransitionToSelf — новый замах РЕЖЕТ предыдущий клип. Раньше
            // повторный триггер посреди удара не брался и оставался взведённым:
            // он выстреливал сразу после возврата в Idle, давая фантомный удар
            // без замаха в модели. Триггер ставится ровно раз на замах (по
            // смене штампа), так что перезапуск здесь — это и есть «начался
            // следующий удар».
            ClearAny(sm, attack);
            ClearOut(attack);
            FitClipToWindow(attack, "AttackSpeed");
            var ai = sm.AddAnyStateTransition(attack);
            ai.AddCondition(AnimatorConditionMode.If, 0, "Attack");
            ai.hasExitTime = false; ai.duration = 0.1f; ai.canTransitionToSelf = true;
            var ao = attack.AddTransition(idle);
            ao.hasExitTime = true; ao.exitTime = 0.9f; ao.duration = 0.15f;

            // §29C.3-hit: like Attack — a trigger, plays once, exits by time. The
            // view fires it only when the NPC is stationary (see SignalHealth).
            // The flinch is a fast beat, not a scene: the clip runs at 1.5x and
            // bails at half — combat reads bite-flinch-strike, and the stagger
            // can never sit on top of the exchange. A pending Attack trigger
            // cuts the flinch IMMEDIATELY (hit → attack, no exit time), so a
            // landed hit never swallows her counter-swing.
            ClearAny(sm, hitReact);
            ClearOut(hitReact);
            hitReact.speed = 1.5f;
            var hi = sm.AddAnyStateTransition(hitReact);
            hi.AddCondition(AnimatorConditionMode.If, 0, "HitReact");
            hi.AddCondition(AnimatorConditionMode.IfNot, 0, "Dead");
            hi.hasExitTime = false; hi.duration = 0.08f; hi.canTransitionToSelf = false;
            var ha = hitReact.AddTransition(attack);
            ha.AddCondition(AnimatorConditionMode.If, 0, "Attack");
            ha.hasExitTime = false; ha.duration = 0.05f;
            var ho = hitReact.AddTransition(idle);
            ho.hasExitTime = true; ho.exitTime = 0.5f; ho.duration = 0.1f;

            // §81: сценка — как Attack: триггер, один проход, выход по времени.
            // canTransitionToSelf=false, иначе повторный триггер посреди клипа
            // рвал бы его с начала. Мертвецы не пляшут.
            ClearAny(sm, emote);
            ClearOut(emote);
            var ei = sm.AddAnyStateTransition(emote);
            ei.AddCondition(AnimatorConditionMode.If, 0, "Emote");
            ei.AddCondition(AnimatorConditionMode.IfNot, 0, "Dead");
            ei.hasExitTime = false; ei.duration = 0.2f; ei.canTransitionToSelf = false;
            // Из сценки выбивает любое настоящее дело: удар, урон, ходьба.
            var ea = emote.AddTransition(attack);
            ea.AddCondition(AnimatorConditionMode.If, 0, "Attack");
            ea.hasExitTime = false; ea.duration = 0.05f;
            var ew = emote.AddTransition(idle);
            ew.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
            ew.hasExitTime = false; ew.duration = 0.15f;
            var eo = emote.AddTransition(idle);
            eo.hasExitTime = true; eo.exitTime = 0.95f; eo.duration = 0.25f;

            // Death: enter on the Dead bool and HOLD (no exit) — the clip should
            // be Loop Time OFF so it freezes on the last frame.
            ClearAny(sm, death);
            ClearOut(death);
            var di = sm.AddAnyStateTransition(death);
            di.AddCondition(AnimatorConditionMode.If, 0, "Dead");
            di.hasExitTime = false; di.duration = 0.1f; di.canTransitionToSelf = false;

            // §50: Crawl — enter from AnyState while Crawling AND not dead (added
            // AFTER death so the death transition is evaluated first); leave back
            // to Idle when Crawling clears. Mirrors the loopy activity pattern.
            ClearAny(sm, crawl);
            ClearOut(crawl);
            var ci = sm.AddAnyStateTransition(crawl);
            ci.AddCondition(AnimatorConditionMode.If, 0, "Crawling");
            ci.AddCondition(AnimatorConditionMode.IfNot, 0, "Dead");
            ci.hasExitTime = false; ci.duration = 0.2f; ci.canTransitionToSelf = false;
            var co = crawl.AddTransition(idle);
            co.AddCondition(AnimatorConditionMode.IfNot, 0, "Crawling");
            co.hasExitTime = false; co.duration = 0.2f;

            // §105: FallDown → (FallenIdle | Sleep) → подъём.
            //
            // ⭐ Вход НЕ через AnyState, и это главное. Первая редакция вешала
            // AnyState→FallDown на «Fallen == true» — а условие остаётся
            // истинным всё время, пока она лежит, поэтому из FallenIdle тот же
            // переход тут же дёргал её обратно в падение: она падала, падала и
            // падала, не доходя до лежачего лупа. AnyState годится для
            // ОДНОГО состояния (Death, Crawl) и ломается на цепочке из трёх.
            // Сонная цепочка LieDown→Sleep→GetUp по этой же причине входит
            // явными переходами из каждого стоячего состояния — делаем так же.
            //
            // Ветвление после приземления — по Laying:
            //   Laying == false → FallenIdle: кома и умирание, тело лежит
            //                     безвольно, пока его не поднимут;
            //   Laying == true  → Sleep: потеряла сознание — рухнула так же, но
            //                     дальше просто спит, и встаёт обычным GetUp.
            var sleep = Find(sm, "Sleep");
            var lieDown = Find(sm, "LieDown");

            ClearAny(sm, fallDown);
            ClearAny(sm, fallenIdle);
            ClearAny(sm, standUp);
            ClearInbound(sm, fallDown);
            ClearInbound(sm, fallenIdle);
            ClearInbound(sm, standUp);
            ClearOut(fallDown);
            ClearOut(fallenIdle);
            ClearOut(standUp);

            // Падение приходит откуда угодно — из бега, из драки, из работы.
            // Кроме: самой цепочки падения (это и был луп), смерти и тех, кто
            // УЖЕ на земле — лежащую не роняют второй раз.
            foreach (var cs in sm.states)
            {
                var s = cs.state;
                if (s == fallDown || s == fallenIdle || s == standUp || s == death ||
                    s == sleep || s == lieDown || s.name == "GetUp")
                {
                    continue;
                }

                var enter = s.AddTransition(fallDown);
                enter.AddCondition(AnimatorConditionMode.If, 0, "Fallen");
                enter.hasExitTime = false;
                enter.duration = 0.15f;
            }

            // Уже спящую (в кровати) кома роняет не клипом падения — она и так
            // лежит; ей достаточно обмякнуть в лежачий луп.
            foreach (var alreadyDown in new[] { sleep, lieDown })
            {
                if (alreadyDown == null)
                {
                    continue;
                }

                var slump = alreadyDown.AddTransition(fallenIdle);
                slump.AddCondition(AnimatorConditionMode.If, 0, "Fallen");
                slump.AddCondition(AnimatorConditionMode.IfNot, 0, "Laying");
                slump.hasExitTime = false;
                slump.duration = 0.25f;
            }

            // Сонная цепочка обязана пропускать падение вперёд — и на входе, и
            // на выходе. Порядок переходов внутри состояния есть порядок
            // вычисления, а эти записаны в контроллере раньше наших:
            //
            //   → LieDown: у потерявшей сознание подняты ОБА флага, и без этого
            //     она уходила бы в аккуратное «прилегла» вместо падения;
            //   → GetUp:   он срабатывает на «Laying == false», а ровно это и
            //     значит «спящую накрыла кома» — она бы ВСТАЛА, чтобы тут же
            //     рухнуть, вместо того чтобы обмякнуть на месте.
            var getUp = Find(sm, "GetUp");
            foreach (var cs in sm.states)
            {
                foreach (var t in cs.state.transitions)
                {
                    if ((lieDown != null && t.destinationState == lieDown) ||
                        (getUp != null && t.destinationState == getUp))
                    {
                        EnsureCondition(t, AnimatorConditionMode.IfNot, "Fallen");
                    }
                }
            }

            // Падение доиграло — лечь. Обе ветки по времени: это одна
            // непрерывная сцена, а не два независимых состояния.
            var fl = fallDown.AddTransition(fallenIdle);
            fl.AddCondition(AnimatorConditionMode.IfNot, 0, "Laying");
            fl.hasExitTime = true; fl.exitTime = 0.95f; fl.duration = 0.3f;

            if (sleep != null)
            {
                var fs = fallDown.AddTransition(sleep);
                fs.AddCondition(AnimatorConditionMode.If, 0, "Laying");
                fs.hasExitTime = true; fs.exitTime = 0.95f; fs.duration = 0.3f;
            }

            // Досрочный подъём прямо из падения: её подняли, пока клип ещё шёл.
            var fu = fallDown.AddTransition(standUp);
            fu.AddCondition(AnimatorConditionMode.IfNot, 0, "Fallen");
            fu.AddCondition(AnimatorConditionMode.IfNot, 0, "Laying");
            fu.hasExitTime = false; fu.duration = 0.2f;

            var lu = fallenIdle.AddTransition(standUp);
            lu.AddCondition(AnimatorConditionMode.IfNot, 0, "Fallen");
            lu.hasExitTime = false; lu.duration = 0.25f;

            var ui = standUp.AddTransition(idle);
            ui.hasExitTime = true; ui.exitTime = 0.9f; ui.duration = 0.25f;

            EditorUtility.SetDirty(ac);
            AssetDatabase.SaveAssets();
            Debug.Log($"[NpcActionStates] Built. states={sm.states.Length} params={ac.parameters.Length} " +
                $"talk={talk.motion != null} gather={gather.motion != null} drink={drink.motion != null} " +
                $"attack={attack.motion != null} death={death.motion != null}");
        }

        static void AddParam(AnimatorController ac, string n, AnimatorControllerParameterType t,
            float defaultFloat = 0f)
        {
            var exists = false;
            foreach (var p in ac.parameters)
            {
                if (p.name == n) exists = true;
            }
            if (!exists) ac.AddParameter(n, t);
            if (defaultFloat == 0f)
            {
                return;
            }
            // AddParameter has no default-value overload, so a float that must
            // NOT start at 0 is patched after the fact — and re-asserted on a
            // re-run, because a SPEED left at 0 freezes the state dead and that
            // must not be one careless controller edit away.
            var all = ac.parameters;
            foreach (var p in all)
            {
                if (p.name == n) p.defaultFloat = defaultFloat;
            }
            ac.parameters = all;
        }

        // §77.5: one interaction = one playthrough. The state plays its clip at
        // ActionSpeed instead of the authored 1, and the view sets that float to
        // clipLength / interactionSeconds — long job, slow motion; short job,
        // brisk. Only states where ONE interaction IS ONE gesture get this:
        // Chop is a REPEATED swing at a tree and must keep looping, and the
        // wardrobe/talk beats have their own timing.
        static void FitClipToWindow(AnimatorState s, string parameter = "ActionSpeed")
        {
            if (s == null) return;
            s.speedParameterActive = true;
            s.speedParameter = parameter;
        }

        static AnimatorState Find(AnimatorStateMachine sm, string n)
        {
            foreach (var cs in sm.states)
            {
                if (cs.state.name == n) return cs.state;
            }
            return null;
        }

        static AnimatorState AddState(AnimatorStateMachine sm, string n, Motion m)
        {
            var s = Find(sm, n) ?? sm.AddState(n);
            if (m != null) s.motion = m;
            else Debug.LogWarning($"[NpcActionStates] No clip for state '{n}' — set it in the state later.");
            return s;
        }

        static AnimationClip Clip(string takeName)
        {
            var fbx = AnimDir + takeName + ".fbx";
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(fbx))
            {
                if (a is AnimationClip c && !c.name.StartsWith("__preview"))
                {
                    return c;
                }
            }
            Debug.LogWarning($"[NpcActionStates] Clip not found: {fbx}");
            return null;
        }

        static void ClearAny(AnimatorStateMachine sm, AnimatorState dst)
        {
            foreach (var t in new List<AnimatorStateTransition>(sm.anyStateTransitions))
            {
                if (t.destinationState == dst) sm.RemoveAnyStateTransition(t);
            }
        }

        static void ClearOut(AnimatorState s)
        {
            foreach (var t in new List<AnimatorStateTransition>(s.transitions))
            {
                s.RemoveTransition(t);
            }
        }

        // §105: снять ВХОДЯЩИЕ переходы из обычных состояний. Нужен, потому что
        // цепочка падения входит не из AnyState, а явными переходами из каждого
        // стоячего состояния — без этого повторный запуск меню добавлял бы их
        // поверх старых, и один и тот же переход копился бы с каждым прогоном.
        static void ClearInbound(AnimatorStateMachine sm, AnimatorState dst)
        {
            foreach (var cs in sm.states)
            {
                foreach (var t in new List<AnimatorStateTransition>(cs.state.transitions))
                {
                    if (t.destinationState == dst) cs.state.RemoveTransition(t);
                }
            }
        }

        // Идемпотентно: условие добавляется, только если его там ещё нет.
        static void EnsureCondition(
            AnimatorStateTransition t, AnimatorConditionMode mode, string parameter)
        {
            foreach (var c in t.conditions)
            {
                if (c.parameter == parameter && c.mode == mode) return;
            }

            t.AddCondition(mode, 0, parameter);
        }

        // §50: the Crawl state as a 1D blend on "Speed" — Prone Idle at 0 (she
        // lies still), Zombie Crawl at 1 (she crawls forward). Idempotent: reuses
        // the existing state/tree and rebuilds its two children.
        static AnimatorState BuildCrawlBlend(AnimatorController ac, AnimatorStateMachine sm)
        {
            var crawl = Find(sm, "Crawl") ?? sm.AddState("Crawl");

            if (crawl.motion is not BlendTree tree)
            {
                tree = new BlendTree
                {
                    name = "CrawlBlend",
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = "Speed",
                    useAutomaticThresholds = false
                };
                AssetDatabase.AddObjectToAsset(tree, ac);
                crawl.motion = tree;
            }

            tree.children = new ChildMotion[0]; // reset, then re-add cleanly
            var prone = Clip("Prone Idle");
            var moving = Clip("Zombie Crawl");
            if (prone != null) tree.AddChild(prone, 0f);
            if (moving != null) tree.AddChild(moving, 1f);
            return crawl;
        }

        // §71: the Walk state as a 1D blend on "Gait" — walk at 0, slow run at
        // 0.5, full run at 1. The colony now moves at 1.5x, and a Defend sprint
        // reaches 3.75x, which the walk cycle alone could only express by
        // playing absurdly fast.
        //
        // THREE children even though only one gait may ever be used: the slots
        // ARE the mechanism. An AnimatorOverrideController swaps clips, not
        // states, so a special locomotion (the §50 crawl) overrides all three
        // slots with the SAME clip — then whatever Gait happens to be, she
        // keeps crawling and can never blend into a run. One speed, no leak.
        // NpcActorView.GaitClipKeys holds the three override keys.
        //
        // Idempotent: reuses the existing state/tree and rebuilds its children.
        // The Idle<->Walk transitions (on "Speed") are left untouched.
        static void BuildGaitBlend(AnimatorController ac, AnimatorStateMachine sm)
        {
            var walk = Find(sm, "Walk");
            if (walk == null)
            {
                Debug.LogWarning("[NpcActionStates] No 'Walk' state — gait blend skipped.");
                return;
            }

            // The existing motion is the plain walk clip; keep it as child 0 so
            // the armed-walk override (which keys off that clip's name) survives.
            var walkClip = walk.motion as AnimationClip;
            if (walk.motion is BlendTree existing && existing.children.Length > 0)
            {
                walkClip = existing.children[0].motion as AnimationClip;
            }

            if (walk.motion is not BlendTree tree)
            {
                tree = new BlendTree
                {
                    name = "GaitBlend",
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = "Gait",
                    useAutomaticThresholds = false
                };
                AssetDatabase.AddObjectToAsset(tree, ac);
                walk.motion = tree;
            }

            tree.blendParameter = "Gait";
            tree.useAutomaticThresholds = false;
            tree.children = new ChildMotion[0]; // reset, then re-add cleanly

            var slowRun = Clip("X Bot@Slow Run");
            var run = Clip("X Bot@Running");
            if (walkClip != null) tree.AddChild(walkClip, 0f);
            if (slowRun != null) tree.AddChild(slowRun, 0.5f);
            if (run != null) tree.AddChild(run, 1f);
            Debug.Log($"[NpcActionStates] GaitBlend: walk={walkClip?.name} " +
                $"slowRun={slowRun?.name} run={run?.name}");
        }

        static void Loopy(AnimatorStateMachine sm, AnimatorState s, AnimatorState idle, string boolParam)
        {
            ClearAny(sm, s);
            ClearOut(s);
            var into = sm.AddAnyStateTransition(s);
            into.AddCondition(AnimatorConditionMode.If, 0, boolParam);
            into.hasExitTime = false; into.duration = 0.15f; into.canTransitionToSelf = false;
            var outT = s.AddTransition(idle);
            outT.AddCondition(AnimatorConditionMode.IfNot, 0, boolParam);
            outT.hasExitTime = false; outT.duration = 0.15f;
        }
    }
}
