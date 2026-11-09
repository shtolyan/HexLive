using System;
namespace HexLive.UnityPresentation.Input
{
/// <summary>Engine-free world-space ray/capsule intersection for §121 body picking.</summary>
public static class NpcPickMath
{
    public readonly struct Point
    {
        public readonly float X,Y,Z;
        public Point(float x,float y,float z) { X=x;Y=y;Z=z; }
        public static Point operator +(Point a,Point b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Point operator -(Point a,Point b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Point operator *(Point a,float s)=>new(a.X*s,a.Y*s,a.Z*s);
        public float Dot(Point b)=>X*b.X+Y*b.Y+Z*b.Z;
    }
    public static bool Capsule(Point origin,Point direction,Point from,Point to,float radius,float maximum,out float distance)
    {
        distance=maximum;
        if(radius<=0||maximum<=0) return false;
        var speed=direction.Dot(direction);
        if(speed<1e-12f) return false;
        direction=direction*(1f/(float)Math.Sqrt(speed));
        var axis=to-from;var offset=origin-from;var length2=axis.Dot(axis);
        var projected=length2>1e-12f?Math.Max(0,Math.Min(1,offset.Dot(axis)/length2)):0;
        var nearest=offset-axis*projected;
        if(nearest.Dot(nearest)<=radius*radius) { distance=0;return true; }
        var found=false;
        if(length2>1e-12f)
        {
            var ad=axis.Dot(direction);var ao=axis.Dot(offset);
            var a=length2-ad*ad;
            var b=length2*direction.Dot(offset)-ao*ad;
            var c=length2*offset.Dot(offset)-ao*ao-radius*radius*length2;
            var discriminant=b*b-a*c;
            if(a>1e-10f&&discriminant>=0)
            {
                var t=(-b-(float)Math.Sqrt(discriminant))/a;var y=ao+t*ad;
                if(t>=0&&t<distance&&y>=0&&y<=length2) {distance=t;found=true;}
            }
        }
        if(Sphere(origin,direction,from,radius,distance,out var first)) {distance=first;found=true;}
        if(Sphere(origin,direction,to,radius,distance,out var second)) {distance=second;found=true;}
        return found;
    }
    private static bool Sphere(Point origin,Point direction,Point center,float radius,float maximum,out float distance)
    {
        distance=maximum;var offset=origin-center;var b=offset.Dot(direction);
        var discriminant=b*b-offset.Dot(offset)+radius*radius;
        if(discriminant<0) return false;
        var t=-b-(float)Math.Sqrt(discriminant);
        if(t<0||t>=maximum) return false;
        distance=t;return true;
    }
}
}
