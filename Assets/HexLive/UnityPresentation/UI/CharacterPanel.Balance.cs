#nullable enable
using System;
using System.Collections;
using System.Globalization;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json;
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
            [JsonProperty(Required = Required.Always)] public decimal openingUsd;
            [JsonProperty(Required = Required.Always)] public decimal balanceUsd;
            [JsonProperty(Required = Required.Always)] public decimal spentUsd;
            [JsonProperty(Required = Required.Always)] public decimal estimatedUsd;
            [JsonProperty(Required = Required.Always)] public int pendingRequests;
        }

        private void BuildBalance()
        {
            _balanceLabel = new Label { name = "accountBalance" };
            _balanceLabel.style.position = Position.Absolute;
            _balanceLabel.style.top = 8f;
            _balanceLabel.style.right = 73f;
            _balanceLabel.style.height = 56f;
            _balanceLabel.style.maxWidth = 245f;
            _balanceLabel.style.color = Text;
            _balanceLabel.style.fontSize = 11f;
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
                    ShowBalanceUnavailable();
                    yield break;
                }
                ApplyBalanceJson(request.downloadHandler.text);
            }
            finally { _balancePending = false; }
        }

        private void ShowBalanceUnavailable()
        {
            if (_balanceLabel == null) return;
            _balanceLabel.style.display = DisplayStyle.Flex;
            _balanceLabel.text = Loc.Get("account.balance.unavailable");
            _balanceLabel.tooltip = _balanceLabel.text;
        }

        private void ApplyBalanceJson(string json)
        {
            if (_balanceLabel == null) return;
            BalanceView? value;
            try { value = JsonConvert.DeserializeObject<BalanceView>(json, new JsonSerializerSettings { FloatParseHandling = FloatParseHandling.Decimal }); }
            catch (JsonException) { ShowBalanceUnavailable(); return; }
            if (value == null || value.openingUsd < 0 || value.spentUsd < 0 || value.estimatedUsd < 0 ||
                value.estimatedUsd > value.spentUsd || value.pendingRequests < 0 ||
                value.openingUsd - value.spentUsd != value.balanceUsd)
            { ShowBalanceUnavailable(); return; }

            string Usd(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
            var cents = decimal.Round(value.spentUsd * 100m, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
            var approximate = value.estimatedUsd > 0 || value.pendingRequests > 0 ? "≈ " : "";
            var lines = string.Format(Loc.Get("account.balance.amount"), approximate + Usd(value.balanceUsd)) + "\n" +
                string.Format(Loc.Get("account.balance.spent"), approximate + Usd(value.spentUsd), cents);
            if (value.pendingRequests > 0) lines += "\n" + string.Format(Loc.Get("account.balance.pending"), value.pendingRequests);
            else if (value.estimatedUsd > 0) lines += "\n" + Loc.Get("account.balance.estimated");
            _balanceLabel.text = lines;
            _balanceLabel.style.display = DisplayStyle.Flex;
            _balanceLabel.tooltip = string.Format(Loc.Get("account.balance.details"),
                Usd(value.openingUsd), Usd(value.spentUsd - value.estimatedUsd), Usd(value.estimatedUsd), value.pendingRequests);
            ApplyServerVoiceBalance((float)value.balanceUsd);
        }
    }
}
