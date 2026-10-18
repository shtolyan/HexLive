using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.History;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the journal (§136) as the page's «journal» screen. RefreshJournal
    /// stays the owner: it rebuilds on a new entry and moves the client-side «read
    /// up to» mark — and remembers where the mark stood before, so the page can
    /// still show which entries were new when the journal was opened.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        private const string HtmlJournal = "journal";
        private string _lastJournalJson;
        private bool _journalHtmlHidden;
        private int _journalShownReadTo = int.MinValue;
        private int _journalModelSig = int.MinValue, _journalModelActor = -1;
        private JObject _journalModel;

        private void UpdateHtmlJournal(WorldSnapshot snapshot)
        {
            if (!_journalOpen)
            {
                if (_lastJournalJson != null) { HtmlUi.Hide(HtmlJournal); _lastJournalJson = null; }
                _journalHtmlHidden = false;
                _journalModel = null;
                return;
            }

            if (!_journalHtmlHidden)
            {
                _journalHtmlHidden = true;
                _journalWindow.style.visibility = Visibility.Hidden;
                if (_journalModal?.Backdrop != null) _journalModal.Backdrop.style.visibility = Visibility.Hidden;
            }

            var npc = FindNpc(snapshot, NpcSelection.PrimaryId, out _);
            if (npc == null) return;
            // The journal changes once a game hour: format it only when it did.
            if (_journalModel == null || _journalModelActor != _journalActorId || _journalModelSig != _journalSig)
            {
                _journalModelActor = _journalActorId;
                _journalModelSig = _journalSig;
                _journalModel = JournalModel(snapshot, npc);
            }
            Push(HtmlJournal, _journalModel, ref _lastJournalJson);
        }

        private JObject JournalModel(WorldSnapshot snapshot, NpcSnapshot npc)
        {
            var entries = FindJournal(snapshot, npc);
            // §58.9: дневник пишется в роде автора, а люди в нём — в своём.
            NpcGender.Remember(snapshot);
            var author = NpcGender.IsMale(npc);
            var list = new JArray();
            var unread = 0;
            // Newest first, as on the UI Toolkit page.
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                var text = JournalFormatter.Format(entry, author);
                var isUnread = entry.Tick > _journalShownReadTo;
                if (isUnread) unread++;
                list.Add(new JObject
                {
                    ["id"] = $"{entry.Tick}:{i}",
                    ["time"] = text.Time,
                    ["text"] = text.Body,
                    ["danger"] = text.Tone == GameHistoryTone.Danger,
                    ["unread"] = isUnread,
                });
            }

            return new JObject { ["name"] = Loc.NpcName(npc.DisplayName), ["unread"] = unread, ["entries"] = list };
        }

        private void OnHtmlJournalCommand(string command, JObject args)
        {
            _htmlDirty = true;
            if (command == "close") CloseJournal();
        }
    }
}
