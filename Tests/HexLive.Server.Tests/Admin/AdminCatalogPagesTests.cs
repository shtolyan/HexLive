using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Server.Admin;
using HexLive.Server.Assets;
using NUnit.Framework;

namespace HexLive.Server.Tests.Admin
{

public sealed class AdminCatalogPagesTests
{
    [Test]
    public void Catalog_PaginatesByTenAndRendersOnlyCurrentPageIcons()
    {
        var records = Enumerable.Range(0, 23).Select(index => new ContentObjectRecord
        {
            Type = "object",
            Id = $"item.test_{index:00}",
            Revision = 1,
            State = "active",
            PublishedAtUtc = DateTimeOffset.UtcNow,
        }).ToArray();
        var html = AdminPages.Catalog(Overview(records), pinnedRevision: 1,
            query: null, type: null, state: null, category: null, page: 2, notice: null);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Показано 11–20 из 23"));
            Assert.That(html, Does.Contain("item.test_10"));
            Assert.That(html, Does.Contain("item.test_19"));
            Assert.That(html, Does.Not.Contain("item.test_09"));
            Assert.That(html, Does.Not.Contain("item.test_20"));
            Assert.That(Count(html, "<img src='/admin/icons/"), Is.EqualTo(10));
            Assert.That(html, Does.Contain("page=3"));
        });
    }

    [Test]
    public void Catalog_SearchesRussianCategoryWordsAndFiltersGarmentCategory()
    {
        var underwear = Wear("underwear.bra_test", "Bra", "Underwear");
        var skirt = Wear("clothing.skirt_test", "Test skirt", "Wear");
        var catalog = new AssetCatalogOverview
        {
            RegistryRevision = 5,
            Records = new[] { underwear.Record, skirt.Record },
            Wear = new[] { underwear, skirt },
            Types = new[] { new AssetCatalogTypeStats { Type = "wear", Active = 2 } },
        };

        var searched = AdminPages.Catalog(catalog, 5, "бельё", null, null, null, 1, null);
        var filtered = AdminPages.Catalog(catalog, 5, null, null, null, "Bottom", 1, null);

        Assert.Multiple(() =>
        {
            Assert.That(searched, Does.Contain("underwear.bra_test"));
            Assert.That(searched, Does.Not.Contain("clothing.skirt_test"));
            Assert.That(filtered, Does.Contain("clothing.skirt_test"));
            Assert.That(filtered, Does.Not.Contain("underwear.bra_test"));
            Assert.That(filtered, Does.Contain("Низ"));
        });
    }

    [Test]
    public void Catalog_PaginationPreservesEscapedFilters()
    {
        var wear = Enumerable.Range(0, 12)
            .Select(index => Wear($"clothing.skirt_test_{index:00}", "Юбка тест", "Wear"))
            .ToArray();
        var catalog = new AssetCatalogOverview
        {
            RegistryRevision = 9,
            Records = wear.Select(value => value.Record).ToArray(),
            Wear = wear,
            Types = new[] { new AssetCatalogTypeStats { Type = "wear", Active = wear.Length } },
        };

        var html = AdminPages.Catalog(catalog, 9, "юбка", "wear", "active", "Bottom", 1, null);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("q=%D1%8E%D0%B1%D0%BA%D0%B0"));
            Assert.That(html, Does.Contain("type=wear&amp;state=active&amp;category=Bottom&amp;page=2"));
            Assert.That(html, Does.Contain("/admin/icons/clothing.skirt_test_00"));
        });
    }

    private static AssetCatalogOverview Overview(IReadOnlyList<ContentObjectRecord> records) => new()
    {
        RegistryRevision = 1,
        Records = records,
        Types = records.GroupBy(value => value.Type).Select(group => new AssetCatalogTypeStats
        {
            Type = group.Key,
            Active = group.Count(),
        }).ToArray(),
    };

    private static GarmentCatalogEntry Wear(string id, string displayName, string layer)
    {
        var record = new ContentObjectRecord
        {
            Type = "wear",
            Id = id,
            Revision = 1,
            State = "active",
            PublishedAtUtc = DateTimeOffset.UtcNow,
        };
        return new GarmentCatalogEntry
        {
            Record = record,
            ConfigurationSource = "record",
            Simulation = new GarmentSimulationMetadata
            {
                DisplayName = displayName,
                PrototypeId = id,
                Layer = layer,
                Sex = "Any",
                Covers = new List<string> { "Pelvis" },
            },
        };
    }

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;
}

}
