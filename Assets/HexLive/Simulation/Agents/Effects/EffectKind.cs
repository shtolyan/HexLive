namespace HexLive.Simulation.Agents.Effects
{
    // Spec §48: the canonical set of status effects (buffs/debuffs) a survivor
    // can be under. Most are DERIVED — the evaluator classifies them each tick
    // from the existing need/body/environment fields. A few timed effects read
    // stored "until tick" fields when the simulation itself needs the state.
    public enum EffectKind
    {
        // ── Injury / blood ────────────────────────────────────────────────
        Bleeding,     // a fresh wound on a badly hurt part is draining blood
        Injured,      // open wounds / a mauled part, but not actively bleeding
        Hobbled,      // leg damage — slower on her feet
        Bandaged,     // a wound is dressed: the bleed is stopped, it's closing
        Maimed,       // spec §50: a limb is gone for good — severed, won't heal
        Adrenaline,   // fresh pain/fear spike — too keyed-up to sleep

        // ── Environment: sun & temperature ────────────────────────────────
        StrongSun,    // standing under a high effective UV index right now
        Sunstroke,    // too long in the sun on bare skin — burning, HP at risk
        Sunburnt,     // raw red skin from the sun (cosmetic)
        Hot,          // uncomfortably warm — mild tier, before heatstroke
        Heatstroke,   // effective temperature far too hot — HP draining
        Cold,         // uncomfortably chilly — mild tier, before freezing
        Freezing,     // effective temperature far too cold — HP draining
        Soaked,       // wet clothes — no warmth from them, heavier on the move
        Cozy,         // §49.8: sat by a lit campfire — its glow eases comfort (buff)

        // ── Survival needs at the danger edge ─────────────────────────────
        Starving,     // hunger past the emergency line — HP draining
        Dehydrated,   // thirst past the emergency line — HP draining
        Sick,         // gut-rot from raw water — torso nibbled over hours (spec §49).
                      // The ONE effect backed by a stored field (Mind.SickUntilTick)
                      // and a real DoT — the rest are pure UI classification.
        Exhausted,    // stamina spent to the floor — winded, wants to sit
        Fainted,      // knocked out — utterly spent, lying unable to act
        Coma,         // spec §60: energy or blood hit 0 — lies as if dead,
                      // healing at sleep pace, until the stat recovers past 15%
        WellFed,      // freshly full (buff)
        Rested,       // energy and stamina both high (buff)
        Snug,         // §54.11: asleep in a proper bed — resting deeply, recovering
                      // energy faster (buff). The bed you built earning its keep.

        // ── Mind & wellbeing ──────────────────────────────────────────────
        Stressed,     // stress climbing toward the breaking point
        Grieving,     // mourning a fallen housemate
        Lonely,       // starved of company
        Miserable,    // comfort bottomed out
        Content,      // comfort high — at ease (buff)

        // ── Hygiene ───────────────────────────────────────────────────────
        Filthy,       // grubby — long overdue a wash

        // ── §105: на грани смерти ─────────────────────────────────────────
        // Дописаны В КОНЕЦ: чип едет на провод строкой "<Kind>\t<Intensity>",
        // но сейв и панель разбирают его по имени, а вставка в середину
        // перемаркировала бы каждый существующий эффект.
        Dying,        // §105: лежит и умирает — интенсивность = сколько запаса
                      // уже вытекло, то есть чип и ЕСТЬ полоска умирания
        Convalescent, // §105: едва живая — вытащили с того света, и несколько
                      // часов она еле ходит и мгновенно выдыхается
        Crying,       // §110: сломалась от стресса — лежит и рыдает. В сознании,
                      // поэтому НЕ Fainted; вытесняет Stressed, пока идёт плач
        PlayingDead   // §105.14: очнулась при враге и не встаёт — лежит и
                      // притворяется трупом, пока он не потеряет к ней интерес
    }

    // Colours the chip ring and sorts the row: buffs read green, debuffs red.
    public enum EffectPolarity
    {
        Buff,
        Debuff
    }

    // Grouping for tooltips/sorting and future filtering. Not gameplay.
    public enum EffectCategory
    {
        Injury,
        Environment,
        Survival,
        Mind,
        Hygiene
    }
}
