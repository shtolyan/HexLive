using System;
using HexLive.UnityPresentation.Content;

namespace HexLive.UnityPresentation.Wearing
{
    // §169: selected by catalog metadata, so old desktop catalogs keep their bodies.
    public static class PeopleIdMap
    {
        public const string CatalogId = "primal-v1";
        public static bool Active => ContentAssetService.Instance.TryGetRecord("actor", "Marta", out var record)
            && (string)record.metadata?["peopleCatalog"] == CatalogId;

        public static string Body(string id) => Active ? Geometry(id) : id;

        public static string Geometry(string id)
        {
            if (!Enum.TryParse(id, true, out ActorName actor) || !Enum.IsDefined(typeof(ActorName), actor)) return id;
            return ActorSex.Of(actor) == VisualGender.Male ? "Kshishtof" : "Marta";
        }

        public static string ContentId(string type, string id) => type == "actor" ? Body(id) : id;
        public static string Kit(string key) => key.StartsWith("actor/", StringComparison.Ordinal)
            ? "actor/" + Body(key.Substring(6)) : key;
    }
}
