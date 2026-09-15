using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Social
{

// Spec 28.1/28.2: per-pair relationships. Authority arrived with §167.
public sealed class SocialState
{
    public Dictionary<EntityId, RelationshipData> Relationships { get; } = new();

    public float Embarrassment { get; set; }

    public RelationshipData GetOrCreate(EntityId other)
    {
        if (!Relationships.TryGetValue(other, out var relationship))
        {
            relationship = new RelationshipData();
            Relationships[other] = relationship;
        }

        return relationship;
    }

    public void MarkInteraction(EntityId other, int tick)
    {
        GetOrCreate(other).LastInteractionTick = tick;
    }
}

public sealed class RelationshipData
{
    public float Trust { get; set; }

    public float Familiarity { get; set; }

    public float Affinity { get; set; }

    /// <summary>
    /// §28.2 / §167.1: насколько Я её слушаю (0..1). Растёт, когда её
    /// указание принято (+AuthorityOnAccept) и выполнено (+AuthorityOnDone),
    /// остывает как дружба. Лидер — та, у кого этот счёт высок у многих;
    /// поля «лидер» нет, это производная.
    /// </summary>
    public float Authority { get; set; }

    /// <summary>§107.8 / Bug #218: latest direct social contact with this person.</summary>
    public int LastInteractionTick { get; set; }
}

// Spec 28.3: what perception carries about another agent.
public sealed class RelationshipSummary
{
    public float Trust { get; set; }

    public float Affinity { get; set; }
}

}
