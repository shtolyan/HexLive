using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.History;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Spec §136: дневник колонистки — кнопка под рюкзаком и страница.
    ///
    /// <para>
    /// Живёт отдельным файлом того же класса, а не своим MonoBehaviour: окно
    /// делит место и взаимное исключение с рюкзаком (§51) и куклой здоровья
    /// (§57), а сцепок между ними ровно столько, что вынести дневник наружу
    /// значило бы протянуть четыре ссылки туда-обратно ради экономии одного
    /// файла.
    /// </para>
    /// </summary>
    public sealed partial class CharacterPanel
    {
        // Бумага и чернила. Намеренно НЕ из общей палитры панели: вся остальная
        // карточка — тёмное неоновое стекло, а это единственное место в игре,
        // куда человек пишет от руки. Оно и должно выглядеть чужим.
        private static readonly Color Paper = new(0.929f, 0.890f, 0.812f);
        private static readonly Color PaperEdge = new(0.788f, 0.718f, 0.604f);
        private static readonly Color Ink = new(0.227f, 0.180f, 0.133f);
        private static readonly Color InkFaint = new(0.478f, 0.416f, 0.337f);
        private static readonly Color InkAlarm = new(0.353f, 0.125f, 0.094f);
        private static readonly Color RuleLine = new(0.353f, 0.290f, 0.216f, 0.16f);
        private static readonly Color Margin = new(0.745f, 0.353f, 0.314f, 0.35f);

        private const float JournalWindowWidth = 620f;
        private const float JournalWindowHeight = 620f;

        private VisualElement _journalButton;
        private Label _journalBadge;
        private VisualElement _journalWindow;
        private Label _journalTitle;
        private Label _journalSubtitle;
        private Label _journalEmpty;
        private ScrollView _journalScroll;
        private JournalDragScrollManipulator _journalDragScroll;
        private VisualElement _journalEntries;
        private Font _journalFont;
        private bool _journalFontLoaded;

        private bool _journalOpen;
        private int _journalActorId = -1;
        private int _journalSig = int.MinValue;

        /// <summary>
        /// «Прочитано до» — тик последней прочитанной записи, по сиду и NPC.
        ///
        /// ⭐ Метка КЛИЕНТА, а не состояние мира (§136.9): на общем сервере
        /// (§83) два зрителя дрались бы за один флаг, и открытая у одного
        /// панель гасила бы бейдж у другого.
        /// </summary>
        private static string ReadKey(int seed, int npcId) =>
            "hexlive.journal.read." + seed + "." + npcId;

        private int LastReadTick(int npcId) =>
            PlayerPrefs.GetInt(ReadKey(_runner != null ? _runner.Seed : 0, npcId), int.MinValue);

        private void MarkJournalRead(int npcId, int tick)
        {
            PlayerPrefs.SetInt(ReadKey(_runner != null ? _runner.Seed : 0, npcId), tick);
        }

        // ── кнопка ──────────────────────────────────────────────────────────

        /// <summary>
        /// §136.9: клон кнопки рюкзака прямо под ним. Оба действия образуют
        /// одну правую колонку, а рюкзак остаётся на привычном верхнем месте.
        /// </summary>
        private VisualElement BuildJournalButton()
        {
            var button = new VisualElement();
            button.style.position = Position.Absolute;
            button.style.right = 12f;
            button.style.top = 78f;
            button.style.width = 58f;
            button.style.height = 58f;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.backgroundColor = new Color(0.014f, 0.058f, 0.074f, 0.94f);
            SetBorder(button, NeonCyanDim, 1.5f);
            SetRadius(button, 18f);
            button.tooltip = Loc.Get("journal.button");

            var inner = new VisualElement();
            inner.style.position = Position.Absolute;
            inner.style.left = 4f;
            inner.style.right = 4f;
            inner.style.top = 4f;
            inner.style.bottom = 4f;
            inner.style.backgroundColor = new Color(0.045f, 0.095f, 0.115f, 0.72f);
            SetBorder(inner, StrokeStrong, 1f);
            SetRadius(inner, 14f);
            inner.pickingMode = PickingMode.Ignore;
            button.Add(inner);

            var accent = new VisualElement();
            accent.style.position = Position.Absolute;
            accent.style.left = 15f;
            accent.style.right = 15f;
            accent.style.bottom = 5f;
            accent.style.height = 2f;
            accent.style.backgroundColor = NeonCyan;
            SetRadius(accent, 1f);
            accent.pickingMode = PickingMode.Ignore;
            button.Add(accent);

            // Иконка-или-глиф, как у рюкзака: отсутствующий PNG не должен
            // убирать контрол.
            var icon = new VisualElement();
            icon.style.width = 46f;
            icon.style.height = 46f;
            icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            icon.pickingMode = PickingMode.Ignore;
            var texture = Resources.Load<Texture2D>("HexLive/UI/IdentityJournalIcon");
            if (texture != null)
            {
                icon.style.backgroundImage = new StyleBackground(texture);
            }
            else
            {
                icon.style.display = DisplayStyle.None;
            }
            button.Add(icon);

            var glyph = new Label("📖");
            glyph.style.fontSize = 26f;
            glyph.style.color = Text;
            glyph.style.display = texture == null ? DisplayStyle.Flex : DisplayStyle.None;
            glyph.pickingMode = PickingMode.Ignore;
            glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.Add(glyph);

            _journalBadge = new Label();
            _journalBadge.style.position = Position.Absolute;
            _journalBadge.style.top = -6f;
            _journalBadge.style.right = -6f;
            _journalBadge.style.minWidth = 20f;
            _journalBadge.style.height = 20f;
            _journalBadge.style.paddingLeft = 5f;
            _journalBadge.style.paddingRight = 5f;
            _journalBadge.style.fontSize = 11f;
            _journalBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            _journalBadge.style.unityTextAlign = TextAnchor.MiddleCenter;
            _journalBadge.style.display = DisplayStyle.None;
            _journalBadge.pickingMode = PickingMode.Ignore;
            SetRadius(_journalBadge, 10f);
            button.Add(_journalBadge);

            button.RegisterCallback<MouseEnterEvent>(_ =>
            {
                button.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.20f);
                inner.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.12f);
                accent.style.backgroundColor = Gold;
                SetBorderColor(button, GoldDim);
            });
            button.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                button.style.backgroundColor = new Color(0.014f, 0.058f, 0.074f, 0.94f);
                inner.style.backgroundColor = new Color(0.045f, 0.095f, 0.115f, 0.72f);
                accent.style.backgroundColor = NeonCyan;
                SetBorderColor(button, NeonCyanDim);
            });
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                ToggleJournal();
                evt.StopPropagation();
            });

            _journalButton = button;
            return button;
        }

        // ── окно ────────────────────────────────────────────────────────────

        private void BuildJournalWindow()
        {
            _journalWindow = new VisualElement();
            _journalWindow.style.position = Position.Absolute;
            _journalWindow.style.bottom = InventoryWindowBottom;
            _journalWindow.style.width = JournalWindowWidth;
            _journalWindow.style.height = JournalWindowHeight;
            _journalWindow.style.backgroundColor = Paper;
            SetBorder(_journalWindow, PaperEdge, 1f);
            SetRadius(_journalWindow, 10f);
            _journalWindow.style.paddingLeft = 34f;
            _journalWindow.style.paddingRight = 26f;
            _journalWindow.style.paddingTop = 20f;
            _journalWindow.style.paddingBottom = 22f;
            _journalWindow.style.overflow = Overflow.Hidden;
            _journalWindow.style.display = DisplayStyle.None;

            // Корешок слева и красная поля-линия: то, что делает лист листом.
            var spine = new VisualElement();
            spine.style.position = Position.Absolute;
            spine.style.left = 0f;
            spine.style.top = 0f;
            spine.style.bottom = 0f;
            spine.style.width = 7f;
            spine.style.backgroundColor = PaperEdge;
            spine.pickingMode = PickingMode.Ignore;
            _journalWindow.Add(spine);

            var marginRule = new VisualElement();
            marginRule.style.position = Position.Absolute;
            marginRule.style.left = 22f;
            marginRule.style.top = 0f;
            marginRule.style.bottom = 0f;
            marginRule.style.width = 1f;
            marginRule.style.backgroundColor = Margin;
            marginRule.pickingMode = PickingMode.Ignore;
            _journalWindow.Add(marginRule);

            // Загнутый угол. UI Toolkit не умеет треугольник, но умеет угловую
            // границу: квадрат нулевого размера с двумя цветными сторонами и
            // есть треугольник — тот же приём, что рисует уголок закладки.
            var fold = new VisualElement();
            fold.style.position = Position.Absolute;
            fold.style.top = 0f;
            fold.style.right = 0f;
            fold.style.width = 0f;
            fold.style.height = 0f;
            fold.style.borderTopWidth = 30f;
            fold.style.borderRightWidth = 30f;
            fold.style.borderTopColor = PaperEdge;
            fold.style.borderRightColor = new Color(0.043f, 0.055f, 0.067f, 0f);
            fold.pickingMode = PickingMode.Ignore;
            _journalWindow.Add(fold);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.FlexEnd;
            header.style.borderBottomWidth = 1f;
            header.style.borderBottomColor = new Color(Ink.r, Ink.g, Ink.b, 0.22f);
            header.style.paddingBottom = 8f;
            header.style.marginBottom = 12f;
            _journalWindow.Add(header);

            _journalTitle = new Label();
            _journalTitle.style.color = Ink;
            _journalTitle.style.fontSize = 19f;
            ApplyHandwriting(_journalTitle);
            header.Add(_journalTitle);

            _journalSubtitle = new Label();
            _journalSubtitle.style.color = InkFaint;
            _journalSubtitle.style.fontSize = 12f;
            _journalSubtitle.style.flexGrow = 1f;
            _journalSubtitle.style.marginLeft = 10f;
            header.Add(_journalSubtitle);

            var close = new Label("✕");
            close.style.width = 24f;
            close.style.height = 24f;
            close.style.color = InkFaint;
            close.style.fontSize = 14f;
            close.style.unityTextAlign = TextAnchor.MiddleCenter;
            close.RegisterCallback<MouseDownEvent>(evt =>
            {
                CloseJournal();
                evt.StopPropagation();
            });
            header.Add(close);

            _journalScroll = new ScrollView(ScrollViewMode.Vertical);
            _journalScroll.style.flexGrow = 1f;
            _journalScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _journalScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _journalScroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Elastic;
            _journalScroll.scrollDecelerationRate = 0.135f;
            _journalScroll.elasticity = 0.1f;
            _journalDragScroll = new JournalDragScrollManipulator(_journalScroll);
            _journalScroll.contentViewport.AddManipulator(_journalDragScroll);
            _journalEntries = new VisualElement();
            _journalEntries.style.flexDirection = FlexDirection.Column;
            _journalScroll.Add(_journalEntries);
            _journalWindow.Add(_journalScroll);

            _journalEmpty = new Label();
            _journalEmpty.style.color = InkFaint;
            _journalEmpty.style.fontSize = 15f;
            _journalEmpty.style.paddingTop = 24f;
            _journalEmpty.style.unityTextAlign = TextAnchor.MiddleCenter;
            _journalEmpty.style.display = DisplayStyle.None;
            ApplyHandwriting(_journalEmpty);
            _journalWindow.Add(_journalEmpty);

            LocalizeJournal();
            _root.Add(_journalWindow);
        }

        /// <summary>
        /// Рукописный шрифт. Если ассета нет — молча остаёмся на дефолтном:
        /// отсутствие файла не должно превращать страницу в пустое окно.
        /// </summary>
        private void ApplyHandwriting(Label label)
        {
            if (!_journalFontLoaded)
            {
                _journalFontLoaded = true;
                _journalFont = Resources.Load<Font>("HexLive/UI/Fonts/Caveat-Regular");
            }

            if (_journalFont != null)
            {
                label.style.unityFontDefinition = new StyleFontDefinition(_journalFont);
            }
        }

        // ── открыть/закрыть ─────────────────────────────────────────────────

        private void ToggleJournal()
        {
            if (_journalOpen)
            {
                CloseJournal();
                return;
            }

            // Три плавающих окна делят одно место под карточкой.
            CloseInventory();
            CloseHealth();

            _journalOpen = true;
            _journalSig = int.MinValue;
            FitJournalWindow();
            _journalWindow.style.display = DisplayStyle.Flex;
            _refreshedTick = -1; // подтянуть свежий снапшот прямо сейчас
        }

        private void CloseJournal()
        {
            _journalOpen = false;
            _journalDragScroll?.Cancel();
            if (_journalWindow != null)
            {
                _journalWindow.style.display = DisplayStyle.None;
            }
        }

        private void FitJournalWindow()
        {
            if (_journalWindow == null || _root == null)
            {
                return;
            }

            var width = _root.layout.width;
            if (width < 1f)
            {
                return;
            }

            _journalWindow.style.left = Mathf.Max(12f, (width - JournalWindowWidth) * 0.5f);
        }

        // ── содержимое ──────────────────────────────────────────────────────

        private void RefreshJournal(WorldSnapshot snapshot, NpcSnapshot npc)
        {
            if (!_journalOpen || _journalEntries == null || npc == null)
            {
                return;
            }

            var entries = FindJournal(snapshot, npc.Id.Value);

            // Дешёвая подпись вместо сравнения содержимого: дневник меняется раз
            // в игровой час, а Refresh зовётся каждый кадр. Пересобирать полсотни
            // элементов на каждом кадре — это мусор на ровном месте.
            var signature = entries.Count * 397 ^
                            (entries.Count == 0 ? 0 : entries[entries.Count - 1].Tick);
            if (_journalActorId == npc.Id.Value && _journalSig == signature)
            {
                return;
            }

            _journalActorId = npc.Id.Value;
            _journalSig = signature;

            _journalEntries.Clear();
            _journalEmpty.style.display = entries.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            var name = Loc.NpcName(npc.DisplayName);
            _journalTitle.text = string.IsNullOrEmpty(name) ? Loc.Get("panel.journal") : name;
            _journalSubtitle.text = string.Format(Loc.Get("journal.subtitle"), name);

            // ⭐ НОВЫЕ СВЕРХУ. Игрок не должен листать вниз, чтобы узнать, что
            // случилось только что: непрочитанное обязано быть первым, что он
            // видит. Кольцо хранится хронологически, поэтому идём с конца.
            var readTo = LastReadTick(npc.Id.Value);
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                _journalEntries.Add(MakeJournalEntry(entries[i], entries[i].Tick > readTo));
            }

            _journalDragScroll?.Cancel();
            _journalScroll.scrollOffset = Vector2.zero;

            if (entries.Count > 0)
            {
                MarkJournalRead(npc.Id.Value, entries[entries.Count - 1].Tick);
                UpdateJournalBadge(snapshot, npc);
            }
        }

        private VisualElement MakeJournalEntry(JournalEntrySnapshot entry, bool unread)
        {
            var text = JournalFormatter.Format(entry);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Column;
            row.style.marginBottom = 14f;
            row.style.paddingBottom = 12f;
            row.style.borderBottomWidth = 1f;
            row.style.borderBottomColor = RuleLine;

            var when = new Label(text.Time);
            when.style.color = InkFaint;
            when.style.fontSize = 11f;
            when.style.marginBottom = 3f;
            row.Add(when);

            var body = new Label(text.Body);
            // Тревожное — темнее и жирнее, но НЕ красное: страница с красными
            // строками перестаёт быть страницей и становится логом.
            body.style.color = text.Tone == GameHistoryTone.Danger ? InkAlarm : Ink;
            body.style.fontSize = 17f;
            body.style.whiteSpace = WhiteSpace.Normal;
            if (text.Tone == GameHistoryTone.Danger)
            {
                body.style.unityFontStyleAndWeight = FontStyle.Bold;
            }
            ApplyHandwriting(body);
            row.Add(body);

            if (unread)
            {
                // Непрочитанное помечено на поле — засечкой, а не цветом текста:
                // цвет уже занят тоном записи.
                var mark = new VisualElement();
                mark.style.position = Position.Absolute;
                mark.style.left = -14f;
                mark.style.top = 4f;
                mark.style.width = 4f;
                mark.style.height = 4f;
                mark.style.backgroundColor = Margin;
                SetRadius(mark, 2f);
                mark.pickingMode = PickingMode.Ignore;
                row.Add(mark);
            }

            return row;
        }

        /// <summary>Циферка на кнопке — сколько записей игрок ещё не читал.</summary>
        private void UpdateJournalBadge(WorldSnapshot snapshot, NpcSnapshot npc)
        {
            if (_journalBadge == null || npc == null)
            {
                return;
            }

            var entries = FindJournal(snapshot, npc.Id.Value);
            var readTo = LastReadTick(npc.Id.Value);

            var unread = 0;
            var alarming = false;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Tick <= readTo)
                {
                    continue;
                }

                unread++;
                if (JournalFormatter.Format(entries[i]).Tone == GameHistoryTone.Danger)
                {
                    alarming = true;
                }
            }

            if (unread == 0)
            {
                _journalBadge.style.display = DisplayStyle.None;
                return;
            }

            _journalBadge.style.display = DisplayStyle.Flex;
            _journalBadge.text = unread > 99 ? "99+" : unread.ToString();
            // Тревожная запись среди непрочитанных видна ещё до открытия окна —
            // это и есть главная работа бейджа: показать, у кого беда.
            _journalBadge.style.backgroundColor = alarming
                ? new Color(0.314f, 0.075f, 0.075f, 0.96f)
                : new Color(0.290f, 0.216f, 0.086f, 0.96f);
            _journalBadge.style.color = alarming ? new Color(0.969f, 0.757f, 0.757f) : Gold;
            SetBorder(_journalBadge, alarming ? new Color(0.886f, 0.294f, 0.290f) : GoldDim, 1f);
        }

        private static readonly List<JournalEntrySnapshot> EmptyJournal = new();

        private static IReadOnlyList<JournalEntrySnapshot> FindJournal(WorldSnapshot snapshot, int npcId)
        {
            if (snapshot?.Journals == null)
            {
                return EmptyJournal;
            }

            for (var i = 0; i < snapshot.Journals.Count; i++)
            {
                if (snapshot.Journals[i].NpcId == npcId)
                {
                    return snapshot.Journals[i].Entries;
                }
            }

            return EmptyJournal;
        }

        private void LocalizeJournal()
        {
            if (_journalButton != null)
            {
                _journalButton.tooltip = Loc.Get("journal.button");
            }

            if (_journalTitle != null)
            {
                _journalTitle.text = Loc.Get("panel.journal");
            }

            if (_journalEmpty != null)
            {
                _journalEmpty.text = Loc.Get("journal.empty");
            }

            // Записи собраны из терминов — на другом языке их надо пересобрать.
            _journalSig = int.MinValue;
        }

        /// <summary>
        /// Мышиный аналог штатного touch-scroll у UI Toolkit. Сам ScrollView
        /// продолжает владеть viewport, клампом и тач-инерцией; этот manipulator
        /// добавляет привычный «схватить страницу» для десктопной мыши.
        /// </summary>
        private sealed class JournalDragScrollManipulator : PointerManipulator
        {
            private const float DragThreshold = 6f;
            private const float StopVelocity = 12f;
            private const float DecelerationPerSecond = 0.135f;

            private readonly ScrollView _scroll;
            private bool _tracking;
            private bool _dragging;
            private int _pointerId = -1;
            private Vector2 _pressPosition;
            private Vector2 _lastPosition;
            private float _lastMoveTime;
            private float _velocity;
            private float _lastInertiaTime;
            private IVisualElementScheduledItem _inertia;

            public JournalDragScrollManipulator(ScrollView scroll)
            {
                _scroll = scroll;
            }

            protected override void RegisterCallbacksOnTarget()
            {
                target.RegisterCallback<PointerDownEvent>(OnPointerDown);
                target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                target.RegisterCallback<PointerUpEvent>(OnPointerUp);
                target.RegisterCallback<PointerCancelEvent>(OnPointerCancel);
                target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
                target.RegisterCallback<WheelEvent>(OnWheel);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                Cancel();
                target.UnregisterCallback<PointerDownEvent>(OnPointerDown);
                target.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
                target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
                target.UnregisterCallback<PointerCancelEvent>(OnPointerCancel);
                target.UnregisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
                target.UnregisterCallback<WheelEvent>(OnWheel);
            }

            public void Cancel()
            {
                StopInertia();
                if (_tracking && target != null && target.HasPointerCapture(_pointerId))
                {
                    target.ReleasePointer(_pointerId);
                }

                _tracking = false;
                _dragging = false;
                _pointerId = -1;
                _velocity = 0f;
            }

            private void OnPointerDown(PointerDownEvent evt)
            {
                // Тач уже реализован самим ScrollView вместе с elastic/inertia.
                // Здесь нужен только такой же жест для основной кнопки мыши.
                if (_tracking ||
                    evt.pointerType != UnityEngine.UIElements.PointerType.mouse || evt.button != 0)
                {
                    return;
                }

                StopInertia();
                _tracking = true;
                _dragging = false;
                _pointerId = evt.pointerId;
                _pressPosition = evt.position;
                _lastPosition = evt.position;
                _lastMoveTime = Time.unscaledTime;
                _velocity = 0f;
            }

            private void OnPointerMove(PointerMoveEvent evt)
            {
                if (!_tracking || evt.pointerId != _pointerId)
                {
                    return;
                }

                var position = (Vector2)evt.position;
                if (!_dragging)
                {
                    if (Vector2.Distance(_pressPosition, position) < DragThreshold)
                    {
                        return;
                    }

                    _dragging = true;
                    target.CapturePointer(_pointerId);
                    _lastPosition = position;
                    _lastMoveTime = Time.unscaledTime;
                    evt.StopPropagation();
                    evt.PreventDefault();
                    return;
                }

                var now = Time.unscaledTime;
                var elapsed = Mathf.Max(0.001f, now - _lastMoveTime);
                var requested = _lastPosition.y - position.y;
                var applied = MoveBy(requested);
                var instantVelocity = applied / elapsed;
                _velocity = Mathf.Lerp(_velocity, instantVelocity, 0.45f);
                _lastPosition = position;
                _lastMoveTime = now;

                evt.StopPropagation();
                evt.PreventDefault();
            }

            private void OnPointerUp(PointerUpEvent evt)
            {
                if (!_tracking || evt.pointerId != _pointerId)
                {
                    return;
                }

                var glide = _dragging && Mathf.Abs(_velocity) >= StopVelocity;
                var velocity = _velocity;
                _tracking = false;
                _dragging = false;
                _pointerId = -1;
                if (target.HasPointerCapture(evt.pointerId))
                {
                    target.ReleasePointer(evt.pointerId);
                }

                if (glide)
                {
                    StartInertia(velocity);
                    evt.StopPropagation();
                    evt.PreventDefault();
                }
            }

            private void OnPointerCancel(PointerCancelEvent evt)
            {
                if (_tracking && evt.pointerId == _pointerId)
                {
                    Cancel();
                }
            }

            private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
            {
                if (_tracking && evt.pointerId == _pointerId)
                {
                    _tracking = false;
                    _dragging = false;
                    _pointerId = -1;
                    _velocity = 0f;
                }
            }

            private static void OnWheel(WheelEvent evt)
            {
                // WheelEvent до родительского ScrollView не доходит, поэтому
                // страница не двигается колесом или двухпальцевым scroll-жестом.
                // Камера читает тот же ввод напрямую из Input System и сохраняет
                // своё исключительное управление zoom/yaw.
                evt.StopPropagation();
            }

            private float MoveBy(float delta)
            {
                var before = _scroll.scrollOffset.y;
                var maximum = Mathf.Max(0f, _scroll.verticalScroller.highValue);
                var after = Mathf.Clamp(before + delta, 0f, maximum);
                _scroll.scrollOffset = new Vector2(_scroll.scrollOffset.x, after);
                return after - before;
            }

            private void StartInertia(float velocity)
            {
                _velocity = velocity;
                _lastInertiaTime = Time.unscaledTime;
                if (_inertia == null)
                {
                    _inertia = target.schedule.Execute(TickInertia).Every(16);
                }
                else
                {
                    _inertia.Resume();
                }
            }

            private void TickInertia()
            {
                var now = Time.unscaledTime;
                var elapsed = Mathf.Clamp(now - _lastInertiaTime, 0.001f, 0.05f);
                _lastInertiaTime = now;
                _velocity *= Mathf.Pow(DecelerationPerSecond, elapsed);

                var moved = MoveBy(_velocity * elapsed);
                if (Mathf.Abs(_velocity) < StopVelocity || Mathf.Abs(moved) < 0.01f)
                {
                    StopInertia();
                }
            }

            private void StopInertia()
            {
                _inertia?.Pause();
                _velocity = 0f;
            }
        }
    }
}
