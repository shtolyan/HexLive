#nullable enable
using System;
using System.Collections;
using System.Globalization;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public sealed partial class CharacterPanel
    {
        private Label? _balanceLabel;
        private float _balanceNextPoll;
        private bool _balancePending;
        [Serializable] private sealed class BalanceView
        {
            public float balanceUsd, spentUsd, estimatedUsd;
            public int pendingRequests;
        }

        private void BuildBalance()
        {
            _balanceLabel = new Label { name = "accountBalance" };
            _balanceLabel.style.position = Position.Absolute;
            _balanceLabel.style.top = 8f;
            _balanceLabel.style.right = 73f;
            _balanceLabel.style.height = 38f;
            _balanceLabel.style.color = Text;
            _balanceLabel.style.fontSize = 12f;
            _balanceLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _balanceLabel.style.display = DisplayStyle.None;
            _root.Add(_balanceLabel);
        }

        private void PollBalance()
        {
            var server = WebVoiceAgent.PageServerHttpBase();
            var bearer = SessionConfig.ControlToken;
            if (!WebVoiceAgent.Supported || string.IsNullOrEmpty(bearer) || server == null)
            {
                if (_balanceLabel != null) _balanceLabel.style.display = DisplayStyle.None;
                return;
            }
            if (_balancePending || Time.unscaledTime < _balanceNextPoll) return;
            _balanceNextPoll = Time.unscaledTime + 3f;
            StartCoroutine(ReadBalance(server, bearer!));
        }

        private IEnumerator ReadBalance(string server, string bearer)
        {
            _balancePending = true;
            try
            {
                using var request = UnityWebRequest.Get(server + "/api/accounts/v1/balance");
                request.SetRequestHeader("Authorization", "Bearer " + bearer);
                request.timeout = 10;
                yield return request.SendWebRequest();
                if (_balanceLabel == null || SessionConfig.ControlToken != bearer) yield break;
                _balanceLabel.style.display = DisplayStyle.Flex;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _balanceLabel.text = "$ —";
                    _balanceLabel.tooltip = Loc.Current == Language.Russian ? "Баланс недоступен" : "Balance unavailable";
                    yield break;
                }
                var value = JsonUtility.FromJson<BalanceView>(request.downloadHandler.text);
                _balanceLabel.text = "$ " + value.balanceUsd.ToString("0.0000", CultureInfo.InvariantCulture) +
                    (value.estimatedUsd > 0 || value.pendingRequests > 0 ? " *" : "") + "\n" +
                    (Loc.Current == Language.Russian ? "Расход $ " : "Spent $ ") +
                    value.spentUsd.ToString("0.0000", CultureInfo.InvariantCulture);
                _balanceLabel.tooltip = Loc.Current == Language.Russian
                    ? $"Расход ${value.spentUsd:0.0000}; по тарифу ${value.estimatedUsd:0.0000}; ждут стоимости: {value.pendingRequests}"
                    : $"Spent ${value.spentUsd:0.0000}; tariff estimate ${value.estimatedUsd:0.0000}; pending: {value.pendingRequests}";
                if (value.balanceUsd <= 0 && ServerVoiceActive) _serverVoice!.Stop();
            }
            finally { _balancePending = false; }
        }
    }
}
