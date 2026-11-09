using System.Collections.Generic;
namespace HexLive.UnityPresentation.Input
{
public enum PointerTargetKind { Person,Object,Wildlife }
public readonly struct PointerTarget
{
    public readonly PointerTargetKind Kind; public readonly int Id; public readonly float Distance;
    public PointerTarget(PointerTargetKind kind,int id,float distance) {Kind=kind;Id=id;Distance=distance;}
}
/// <summary>§121/#647: preserve every distinct hit, deduplicate context proxies, stable nearest-first menu.</summary>
public sealed class PointerTargetSet
{
    private readonly List<PointerTarget> _items=new();
    public IReadOnlyList<PointerTarget> Items=>_items;
    public void Clear()=>_items.Clear();
    public void Add(PointerTargetKind kind,int id,float distance)
    {
        if(float.IsNaN(distance)||float.IsInfinity(distance)||distance<0) return;
        for(var i=0;i<_items.Count;i++)
        {
            var old=_items[i];
            if(old.Kind!=kind||old.Id!=id) continue;
            if(distance<old.Distance) _items[i]=new(kind,id,distance);
            return;
        }
        _items.Add(new(kind,id,distance));
    }
    public void Sort()=>_items.Sort((a,b)=>{
        var c=a.Distance.CompareTo(b.Distance); if(c!=0)return c;
        c=a.Kind.CompareTo(b.Kind);return c!=0?c:a.Id.CompareTo(b.Id);
    });
}
}
