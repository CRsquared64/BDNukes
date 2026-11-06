using System;
using BDNukes;

// Minimal value-only stand-ins let the production orbital math run without starting Unity.
namespace UnityEngine { }
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x=x; this.y=y; this.z=z; }
    public static Vector3d zero => new Vector3d();
    public double sqrMagnitude => x*x+y*y+z*z;
    public double magnitude => Math.Sqrt(sqrMagnitude);
    public Vector3d normalized => magnitude > 0 ? this/magnitude : zero;
    public Vector3d xzy => new Vector3d(x,z,y);
    public static Vector3d operator +(Vector3d a,Vector3d b)=>new Vector3d(a.x+b.x,a.y+b.y,a.z+b.z);
    public static Vector3d operator -(Vector3d a,Vector3d b)=>new Vector3d(a.x-b.x,a.y-b.y,a.z-b.z);
    public static Vector3d operator -(Vector3d a)=>a*(-1);
    public static Vector3d operator *(Vector3d a,double b)=>new Vector3d(a.x*b,a.y*b,a.z*b);
    public static Vector3d operator *(double b,Vector3d a)=>a*b;
    public static Vector3d operator /(Vector3d a,double b)=>a*(1/b);
    public static double Dot(Vector3d a,Vector3d b)=>a.x*b.x+a.y*b.y+a.z*b.z;
    public static Vector3d Cross(Vector3d a,Vector3d b)=>new Vector3d(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
}
public class Orbit
{
    public Vector3d position, velocity;
    public Vector3d getRelativePositionAtUT(double t)=>position+velocity*t;
    public Vector3d getOrbitalVelocityAtUT(double t)=>velocity;
}
public struct Vector2d
{
    public double x,y;
    public Vector2d(double x,double y){this.x=x;this.y=y;}
}
public class Planetarium { public static Planetarium fetch = new Planetarium(); public CelestialBody Sun; }
public static class FlightGlobals { public static System.Collections.Generic.List<CelestialBody> Bodies = new System.Collections.Generic.List<CelestialBody>(); }
public class CelestialBody
{
    public object pqsController;
    public bool ocean, rotates;
    public double rotationPeriod=21600, gravParameter=3.5316e12, Radius=600000;
    public string bodyName="Test";
    public Vector3d position;
    public Orbit orbit;
    public CelestialBody referenceBody;
    public bool atmosphere;
    public double atmosphereDepth, inverseRotThresholdAltitude, sphereOfInfluence=double.PositiveInfinity, atmospherePressureSeaLevel;
    public Vector3d GetRelSurfacePosition(double lat,double lon,double alt)=>GetWorldSurfacePosition(lat,lon,alt)-position;
    public double GetLatitude(Vector3d r,bool relative)=>Math.Asin(r.y/r.magnitude)*180/Math.PI;
    public double GetLongitude(Vector3d r,bool relative)=>Math.Atan2(r.z,r.x)*180/Math.PI;
    public double TerrainAltitude(double a,double b,bool c)=>0;
    public Vector3d GetWorldSurfacePosition(double latitude,double longitude,double height)
    {
        double lat=latitude*Math.PI/180,lon=longitude*Math.PI/180;
        return position+new Vector3d(Math.Cos(lat)*Math.Cos(lon),Math.Sin(lat),Math.Cos(lat)*Math.Sin(lon))*(Radius+height);
    }
    public Vector3d getRFrmVel(Vector3d r)=>Vector3d.Cross(new Vector3d(0,1,0),r-position)*(2*Math.PI/rotationPeriod);
}
class Program
{
    static void Check(bool value,string name) { if(!value) throw new Exception(name); Console.WriteLine("PASS "+name); }
    static Vector3d Integrate(Vector3d r,Vector3d v,double mu,double time)
    {
        double dt=time/20000;
        for(int i=0;i<20000;i++)
        {
            var a=-r.normalized*mu/r.sqrMagnitude;
            r+=v*dt+a*(dt*dt/2);
            var next=-r.normalized*mu/r.sqrMagnitude;
            v+=(a+next)*(dt/2);
        }
        return r;
    }
    static void Main()
    {
        const double mu=3.5316e12,radius=700000;
        var start=new Vector3d(radius,0,0); var end=new Vector3d(0,radius,0); var normal=new Vector3d(0,0,1);
        double quarter=Math.PI/2*Math.Sqrt(radius*radius*radius/mu);
        Check(NukeStrikeMath.Lambert(start,end,quarter,mu,normal,out var v,out _),"quarter orbit solution");
        Check(Math.Abs(v.x)<0.001 && Math.Abs(v.y-Math.Sqrt(mu/radius))<0.001,"analytic circular velocity");
        foreach(double duration in new[]{40d,quarter,quarter*2,quarter*3})
        {
            Check(NukeStrikeMath.Lambert(start,end,duration,mu,normal,out v,out _),"transfer "+duration);
            Check((Integrate(start,v,mu,duration)-end).magnitude<20,"independent integrated arrival "+duration);
        }
        Check(!NukeStrikeMath.Lambert(start,end,0,mu,normal,out _,out _),"reject zero duration");
        Check(!NukeStrikeMath.Lambert(start,start,100,mu,normal,out _,out _),"reject degenerate angle");
        Check(Math.Abs(NukeStrikeMath.CentralAngle(0,179,0,-179)-Math.PI/90)<1e-10,"dateline distance");
        var body=new CelestialBody{rotates=true};
        var rel=new Vector3d(body.Radius,0,0);
        var rotated=NukeStrikeMath.RotateWithBody(body,rel,body.rotationPeriod/4);
        Check((rotated-new Vector3d(0,0,-body.Radius)).magnitude<1e-6,"surface rotation handedness");
        var target=new StrikeTarget {body=body,lat=0,lon=179,height=100,name="Test point"};
        Check(Math.Abs(target.Rel.magnitude-body.Radius-100)<1e-6,"surface target altitude");
        foreach(var centre in new[]{new Vector2d(0,179),new Vector2d(89,-179),new Vector2d(-89,0)})
        {
            var pattern=InterplanetaryMath.AimPattern(centre.x,centre.y,10,30,0);
            Check(pattern.Length==10 && pattern[0].x==centre.x && pattern[0].y==centre.y,"pattern centre");
            for(int i=1;i<10;i++)
                Check(Math.Abs(NukeStrikeMath.CentralAngle(centre.x,centre.y,pattern[i].x,pattern[i].y)-Math.PI/6)<1e-9,"ring radius "+i);
        }
        body.atmosphere=true;body.atmosphereDepth=70000;
        Check(InterplanetaryMath.ParkingAltitude(body,0)==100000,"Kerbin parking altitude");
        var sun=new CelestialBody();Planetarium.fetch.Sun=sun;
        var planet=new CelestialBody {referenceBody=sun,orbit=new Orbit {position=new Vector3d(1e9,0,0)},sphereOfInfluence=1e7};
        var moon=new CelestialBody {referenceBody=planet,orbit=new Orbit {position=new Vector3d(1e6,0,0)},sphereOfInfluence=1e5};
        FlightGlobals.Bodies.AddRange(new[]{sun,planet,moon});
        Check(InterplanetaryMath.SunPos(moon,0).x==1.001e9,"nested Sun-frame position");
        Check(InterplanetaryMath.DominantBody(new Vector3d(1.001e9,0,0),0)==moon,"innermost SOI");
        Check(InterplanetaryMath.DominantBody(new Vector3d(1e9,0,0),0)==planet,"planet SOI");
        Check(InterplanetaryMath.DominantBody(new Vector3d(2e9,0,0),0)==sun,"Sun SOI");
    }
}
