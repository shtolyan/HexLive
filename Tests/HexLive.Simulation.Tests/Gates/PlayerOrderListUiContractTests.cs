using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §121.13: the UI half of the player's action list — an own picture per kind
/// of order, a localized hover caption with the target, and drag-to-reorder
/// through the simulation command. Unity code is not compiled in this
/// assembly, so the sources are checked as text like the other UI contracts.
/// </summary>
public sealed class PlayerOrderListUiContractTests
{
    private static string Repo(params string[] parts) =>
        Path.Combine(new[] { RepoPaths.Root }.Concat(parts).ToArray());

    private static string Orders() => RepoPaths.ReadText(Repo(
        "Assets", "HexLive", "UnityPresentation", "UI", "CharacterPanel.PlayerOrders.cs"));

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), signature);
        var end = source.IndexOf("\n        };", start, StringComparison.Ordinal);
        return source.Substring(start, end - start);
    }

    private static string[] IconSlugs()
    {
        var source = Orders();
        var body = Method(source, "internal static string PlayerOrderIcon(") +
                   Method(source, "private static string InteractionIcon(");
        return Regex.Matches(body, "=> \"([a-z_]+)\"").Select(m => m.Groups[1].Value).Distinct().ToArray();
    }

    // Every action name the queue can hold: OrderName of each command
    // QueuePlayerOrderCommand.Supports accepts.
    private static readonly string[] QueueableActions =
    {
        "MoveTo", "Interact", "PlantSeed", "PlaceInventoryFurniture", "GatherAll", "AttackNpc",
        "AttackMob", "CarryPerson", "PutDownPerson", "PutPersonInBed", "Craft", "TalkTo",
        "RequestItem", "Romance", "ForceRomance", "Aid", "TreatLimbs", "MedicalAid", "SelfAction",
        "ButcherPerson", "Prey", "Abuse", "KnockDoor", "BreakDoor", "FillVessel", "Inventory",
    };

    [Test]
    public void EveryKindOfQueuedOrderHasItsOwnPlayerResourcePicture()
    {
        var source = Orders();
        var icon = Method(source, "internal static string PlayerOrderIcon(");
        var slugs = IconSlugs();
        Assert.Multiple(() =>
        {
            foreach (var action in QueueableActions)
                Assert.That(icon, Does.Contain("\"" + action + "\""), action + " needs its own icon case");
            foreach (var hug in new[] { "\"Hug\" => \"hug\"", "\"Kiss\" => \"kiss\"" })
                Assert.That(icon, Does.Contain(hug), "Hug and kiss are not a plain talk");
            Assert.That(Method(source, "private static string InteractionIcon("), Does.Contain("_ => \"use\""),
                "Every InteractionType has a picture, specific or the generic «use».");
            Assert.That(slugs.Length, Is.GreaterThanOrEqualTo(30), "One picture per kind, not a handful");

            foreach (var slug in slugs)
            {
                var png = Repo("Assets", "Resources", "HexLive", "UI", "ActionIcons", slug + ".png");
                Assert.That(File.Exists(png), Is.True, png);
                if (!File.Exists(png)) continue;
                var bytes = File.ReadAllBytes(png);
                var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                Assert.That((width, height, (int)bytes[25]), Is.EqualTo((128, 128, 6)),
                    slug + ": 128×128 RGBA so the white glyph can be tinted");
                var meta = RepoPaths.ReadText(png + ".meta");
                Assert.That(meta, Does.Contain("textureType: 8"), slug + " imports as a Sprite");
                Assert.That(Regex.IsMatch(meta, @"guid: [0-9a-f]{32}\n"), Is.True, slug);
            }

            var guids = slugs.Select(s => Regex.Match(RepoPaths.ReadText(Repo("Assets", "Resources", "HexLive", "UI",
                "ActionIcons", s + ".png.meta")), "guid: ([0-9a-f]{32})").Groups[1].Value).ToArray();
            Assert.That(guids.Distinct().Count(), Is.EqualTo(guids.Length), "Every icon has its own GUID");

            var generator = RepoPaths.ReadText(Repo("Tools", "gen_action_icons.py"));
            foreach (var slug in slugs)
                Assert.That(generator, Does.Contain("\"" + slug + "\": "), slug + " is drawn by the committed generator");
        });
    }

    [Test]
    public void BothCardsLoadTheGlyphsFromResourcesWithoutABundle()
    {
        var source = Orders();
        var page = RepoPaths.ReadText(Repo("Assets", "WebGLTemplates", "HexLive", "ui", "screens", "character.js"));
        var css = RepoPaths.ReadText(Repo("Assets", "WebGLTemplates", "HexLive", "ui", "theme.css"));
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("internal const string PlayerOrderIconFolder = \"HexLive/UI/ActionIcons/\";"));
            Assert.That(source, Does.Contain("Resources.Load<Texture2D>(PlayerOrderIconFolder + slug)"));
            Assert.That(source, Does.Contain("glyph.style.unityBackgroundImageTintColor = tint;"),
                "The white glyph takes the chip colour (active = green).");
            Assert.That(source, Does.Contain("HtmlUi.Asset(key,"), "The WebGL page receives the same PNG as a data URL");
            Assert.That(source, Does.Contain("[\"icon\"] = HtmlPlayerOrderIcon(PlayerOrderIcon(order))"));
            Assert.That(page, Does.Contain("const url = asset(icon);"));
            Assert.That(css, Does.Contain("mask: var(--glyph) center / contain no-repeat;"));
            Assert.That(source.Any(c => char.IsSurrogate(c) || (c >= '☀' && c <= '➿')), Is.False, "No emoji or symbol stand-ins in the action list");
        });
    }

    [Test]
    public void HoverShowsTheLocalizedActionAndItsTarget()
    {
        var source = Orders();
        var page = RepoPaths.ReadText(Repo("Assets", "WebGLTemplates", "HexLive", "ui", "screens", "character.js"));
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("string.Format(Loc.Get(\"panel.orders.with_target\"), label, target)"));
            Assert.That(source, Does.Contain("order.TargetNpcId is { } npcId"));
            Assert.That(source, Does.Contain("order.TargetObjectId is { } objectId"));
            Assert.That(source, Does.Contain("\"Hug\" => \"menu.hug\""));
            Assert.That(source, Does.Contain("ShowPlayerOrderTooltip(caption, order, texture, item)"),
                "The native card reuses the shared tooltip card");
            Assert.That(source, Does.Contain("PopTooltipAbove(anchor);"));
            Assert.That(page, Does.Contain("...${tip(() => ({ icon: html`<${OrderGlyph} icon=${order.icon} />`, title: order.label, desc: order.hint,"),
                "The page reuses its one tooltip layer");
            Assert.That(page, Does.Not.Contain("title=${order.label}"), "No second, browser-native tooltip");
        });

        var terms = RepoPaths.ReadText(Repo("Assets", "Resources", "I2Languages.asset"));
        foreach (var key in new[]
                 {
                     "panel.orders.with_target", "panel.orders.hint_pending", "panel.orders.hint_active",
                     "panel.orders.explore", "panel.orders.request_item",
                     "toast.order_rejected.QueueChanged", "toast.order_rejected.OrderRunning",
                 })
        {
            var match = Regex.Match(terms, @"- Term: '?" + Regex.Escape(key) + @"'?\n(?:.*\n){3}      - (.*)\n      - (.*)\n");
            Assert.That(match.Success, Is.True, key);
            if (key != "panel.orders.with_target")
                Assert.That(Regex.IsMatch(match.Groups[2].Value, "[А-Яа-яЁё]"), Is.True, key + " needs Russian");
        }
    }

    [Test]
    public void DraggingSendsTheStableIdReorderCommandFromBothCards()
    {
        var source = Orders();
        var html = RepoPaths.ReadText(Repo("Assets", "HexLive", "UnityPresentation", "UI", "CharacterPanel.Html.cs"));
        var page = RepoPaths.ReadText(Repo("Assets", "WebGLTemplates", "HexLive", "ui", "screens", "character.js"));
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("new MovePlayerOrderCommand(actor, orderId, beforeOrderId)"));
            Assert.That(source, Does.Contain("if (order.Active) return;"), "The running order does not drag");
            Assert.That(source, Does.Contain("beforeOrderId = slot < orders.Count ? orders[slot].Id : 0;"));
            Assert.That(source, Does.Contain("CancelPlayerOrder(actorId, order.Id);"), "A click still cancels");
            Assert.That(html, Does.Contain("case \"moveOrder\": EnqueueMovePlayerOrder("));
            Assert.That(page, Does.Contain("draggable=${!order.active}"));
            Assert.That(page, Does.Contain("send('moveOrder', { id: drag, before: slot < orders.length ? orders[slot].id : 0 });"));
            Assert.That(page, Does.Contain("send('cancelOrder', { id: order.id })"));
        });
    }
}
