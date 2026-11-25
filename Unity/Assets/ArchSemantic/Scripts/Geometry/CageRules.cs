using System;
using System.Linq;
using ArchSemanticGeometry;
public static class CageRules
{
    public static double Ease(double p) { p=Math.Max(-1,Math.Min(1,p)); return p*(3-p*p)/2; }
    // Courtyard, room footprints and storey heights.
    // Indices: courtyard W/D; F west/south; R west/south; G W/E/S/N; R/G/F/roof Z.
    public static double[] Dimensions(int s, double p, double q, double r)
    {
        double af=s==0?109.90:109.50, ar=s==0?115.12:115.00;
        double w=s==0?10+(p<0?1.07:1.36)*p-.80*q-.60*r:7+.55*p-.45*q-.30*r;
        double h=s==0?10-p+q+.65*r:14-1.10*p+.90*q+.60*r;
        double fs=s==0?-af/w:-(7.30-.88*p-.25*q-.20*r);
        double fl=s==0?0:w+af/fs;
        double rs=s==0?-4.39-.60*p+.50*q+.30*r:0;
        double rl=-ar/(h-rs);
        double gs=(s==0?-4.39:-4.10)-.70*p+.25*q+1.10*r;
        double gw=(s==0?-4.00:-4.40)-.60*p+.25*q-r;
        double gn=h+(s==0?5.04:5.3967391304)+.50*p-.30*q+.45*r;
        double ge=(226+w*(h-gs)+gw*(gn-h))/(gn-gs);
        double zr=3.60-.15*p-.40*q+.10*r, zg=6-.25*p-.10*q+.35*r;
        double zf=(af*8.4+ar*3.6+226*6-ar*zr-226*zg)/af;
        return new[]{w,h,fl,fs,rl,rs,gw,ge,gs,gn,zr,zg,zf,Math.Max(9,Math.Max(zf+.60,zg+2.85))};
    }
    public static double Remap(double v, double[] a, double[] b)
    {
        int i=0; while(i<a.Length-2 && v>a[i+1]) i++;
        return b[i]+(v-a[i])/(a[i+1]-a[i])*(b[i+1]-b[i]);
    }
    public static Point3d Map(Point3d p,string owner,double[] a,double[] b,double t,double zScale)
    {
        double x,y;
        if(owner=="F") {
            x=a[2]<-.001?Remap(p.X,new[]{a[2],0.0,a[0]},new[]{b[2],0.0,b[0]}):p.X*b[0]/a[0];
            y=p.Y*b[3]/a[3];
        } else if(owner=="R" || owner.StartsWith("U_R")) {
            x=p.X*b[4]/a[4];
            y=a[5]<-.001?Remap(p.Y,new[]{a[5],0.0,a[1]},new[]{b[5],0.0,b[1]}):p.Y*b[1]/a[1];
        } else {
            x=Remap(p.X,new[]{a[6],0.0,a[0],a[7]},new[]{b[6],0.0,b[0],b[7]});
            y=Remap(p.Y,new[]{a[8],0.0,a[1],a[9]},new[]{b[8],0.0,b[1],b[9]});
        }
        double z=Remap(p.Z,new[]{0.0,a[10],a[10]+.30,a[11],a[11]+.30,a[12],a[13]+.18},
            new[]{0.0,b[10],b[10]+.30,b[11],b[11]+.30,b[12],b[13]+.18});
        double scale=1+t*z/(b[13]+.18);
        return new Point3d(b[0]/2+(x-b[0]/2)*scale,b[1]/2+(y-b[1]/2)*scale,z*zScale);
    }
    public static double Taper(double[] q) { return Math.Max(-.18,Math.Min(.18,.10*q[0]-.10*q[1]+.08*q[2])); }
    public static double[] Q(double[] p) { return p.Select(Ease).ToArray(); }
    public static double[] D(int s,double[] p) { var q=Q(p);return Dimensions(s,q[0],q[1],q[2]); }
    static readonly int[,] Tri={{0,2,1},{0,3,2},{4,5,6},{4,6,7},{0,1,5},{0,5,4},{1,2,6},{1,6,5},{2,3,7},{2,7,6},{3,0,4},{3,4,7}};
    public static Point3d[] Corners(double x0,double y0,double x1,double y1,double height)
    { return new[]{new Point3d(x0,y0,0),new Point3d(x1,y0,0),new Point3d(x1,y1,0),new Point3d(x0,y1,0),new Point3d(x0,y0,height),new Point3d(x1,y0,height),new Point3d(x1,y1,height),new Point3d(x0,y1,height)}; }
    static double Volume(Point3d[] v)
    {
        double n=0;
        for(int i=0;i<12;i++){var a=v[Tri[i,0]];var b=v[Tri[i,1]];var c=v[Tri[i,2]];
            n+=a.X*(b.Y*c.Z-b.Z*c.Y)+a.Y*(b.Z*c.X-b.X*c.Z)+a.Z*(b.X*c.Y-b.Y*c.X);}
        return Math.Abs(n/6);
    }
    public static double TargetVolume(int s) { return (s==0?109.90:109.50)*8.4+(s==0?115.12:115)*3.6+226*6; }
    public static double Envelope(int s,double[] p,double zScale)
    {
        var a=Dimensions(s,0,0,0);var b=D(s,p);double t=Taper(Q(p));
        var boxes=new[]{Corners(a[2],a[3],a[0],0,a[12]),Corners(a[4],a[5],0,a[1],a[10]),
            Corners(a[0],a[8],a[7],a[1],a[11]),Corners(a[6],a[1],a[7],a[9],a[11])};
        string[] owners={"F","R","G","G"};
        return boxes.Select((v,i)=>Volume(v.Select(x=>Map(x,owners[i],a,b,t,zScale)).ToArray())).Sum();
    }
    public static double VerticalScale(int s,double[] p) { return TargetVolume(s)/Envelope(s,p,1); }
    // P1: doorway + reveal wall; P2: shelter screen + prospect; P3: shared opening + display wall.
    public static Point3d Local(Point3d p,string role,double[] a,double[] q)
    {
        string portal=role.Length>=3?role.Substring(0,3):"";
        if(portal=="EF_" || portal=="FC_" || portal=="RC_" || portal=="GC_") {
            bool y=portal=="RC_";double start=y?a[5]:portal=="FC_"||portal=="EF_"?a[2]:a[6], end=y?a[1]:a[0];
            double center=portal=="FC_"?a[0]/2:(start+end)/2, delta=portal=="FC_"||portal=="EF_"?(q[0]<0?.20:.80)*q[0]:portal=="RC_"?-.20*q[1]:(q[2]<0?.20:.60)*q[2];
            double half=(2.4+delta)/2;
            double v=Remap(y?p.Y:p.X,new[]{start,center-1.2,center+1.2,end},new[]{start,center-half,center+half,end});
            if(y)p.Y=v;else p.X=v;
        }
        int axis=role=="P1_REVEAL"?0:role.StartsWith("P2_SCREEN")?1:role=="P3_DISPLAY"?2:-1;
        if(axis<0)return p;
        double cx=axis==0?a[2]+(a[0]-a[2])*.24:axis==1?a[4]*.62:a[7]-.8;
        double cy=axis==0?a[3]+3.1:axis==1?a[1]*.45:a[1]*.65;
        if(role=="P2_SCREEN_F"){cx=a[2]+(a[0]-a[2])*.72;cy=a[3]*.55;}
        if(role=="P2_SCREEN_G"){cx=a[7]-.8;cy=a[8]+(a[1]-a[8])*.25;}
        double angle=(axis==0?-15:axis==1?12:-8)*q[axis]*Math.PI/180;
        double x=p.X-cx,y0=p.Y-cy;
        p.X=cx+x*Math.Cos(angle)-y0*Math.Sin(angle);
        p.Y=cy+x*Math.Sin(angle)+y0*Math.Cos(angle)+(axis==0?-1.2*q[0]:axis==1?.5*q[1]:-.5*q[2]);
        p.Z*=1+(axis==0?.125:axis==1?.125:.125)*q[axis];return p;
    }
    public static Point3d Transform(Point3d p,string name,int s,double[] state)
    {
        var tag=name.Split(new[]{"__"},StringSplitOptions.None);var a=Dimensions(s,0,0,0);var b=D(s,state);var q=Q(state);
        return Map(Local(p,tag[3],a,q),tag[2],a,b,Taper(q),VerticalScale(s,state));
    }
    // Fixtures follow their host position while their authored dimensions remain fixed.
    public static Point3d TargetCorner(Point3d p,Point3d center,string name,int s,double[] state)
    {
        var tag=name.Split(new[]{"__"},StringSplitOptions.None);
        if(tag[3]!="DESK"&&tag[3]!="BENCH"&&tag[3]!="PLINTH")return Transform(p,name,s,state);
        center.Z=0;return Transform(center,name,s,state)+(p-center);
    }
    public static double PortalWidth(int s,double[] state,string kind)
    {
        var a=Dimensions(s,0,0,0);bool rc=kind=="RC_";
        double center=rc?(a[5]+a[1])/2:kind=="FC_"?a[0]/2:kind=="EF_"?(a[2]+a[0])/2:(a[6]+a[0])/2;
        double result=double.MaxValue;
        foreach(double z in new[]{.06,2.4}) {
            double y=kind=="EF_"?a[3]:kind=="FC_"?0:a[1];
            var l=rc?new Point3d(0,center-1.2,z):new Point3d(center-1.2,y,z);
            var h=rc?new Point3d(0,center+1.2,z):new Point3d(center+1.2,y,z);
            string name="A__PART__"+(rc?"R":kind=="FC_"||kind=="EF_"?"F":"G")+"__"+kind+"WALL__0";
            result=Math.Min(result,Transform(l,name,s,state).DistanceTo(Transform(h,name,s,state)));
        }return result;
    }

    // Check final mapped dimensions, including taper and volume compensation.
    static bool MappedWidthWithinLimits(Point3d from,Point3d to,string owner,
        double[] basis,double[] target,double taper,double verticalScale)
    {
        double width=Map(from,owner,basis,target,taper,verticalScale)
            .DistanceTo(Map(to,owner,basis,target,taper,verticalScale));
        return width>=3 && width<=4.2;
    }
    public static bool SpatialLimits(int s,double[] p)
    {
        var a=Dimensions(s,0,0,0);var b=D(s,p);
        double w=b[0],h=b[1],fl=b[2],rs=b[5],gw=b[6],ge=b[7],gs=b[8],gn=b[9];
        if(w<6 || h<6 || ge-w<6 || gn-h<4.5 || h-gs<9 || gw>=0 || gs>=0) return false;
        double k=VerticalScale(s,p),t=Taper(Q(p));
        if(double.IsNaN(k) || double.IsInfinity(k) || k<=0) return false;
        // Heights use the mapped reference floor and roof underside; Legal also checks clearances.
        if(b[10]*k<3 || b[10]*k>4.3 || b[12]*k<7 || b[12]*k>10 || (b[12]-b[11])*k<1.1) return false;
        if((b[11]-b[10]-.30)*k<1.8-1e-9 || (b[13]-b[11]-.30)*k<2.25-1e-9) return false;
        // Shared host length: 2.02 m portal plus 0.40 m total wall returns.
        double minScale=Math.Min(1,1+t*b[13]/(b[13]+.18));
        double hostMin=2.02/minScale+.40;
        if(-gw<hostMin || (s==0?-rs:-fl)<hostMin || -gs<hostMin) return false;
        double west=Math.Max(-3.6,a[4]),east=Math.Min(a[0]+3.6,a[7]);
        double north=Math.Min(a[1]+3.6,a[9]);
        // Both west levels, then east and north: include floor and roof-slab extents.
        // The scale varies monotonically with height, so these endpoints bound each strip.
        foreach(double z in new[]{3.9,6.18,6.3,9.18})
            if(!MappedWidthWithinLimits(new Point3d(west,0,z),new Point3d(0,0,z),"U_R",a,b,t,k)) return false;
        foreach(double z in new[]{6.3,9.18}) {
            if(!MappedWidthWithinLimits(new Point3d(a[0],0,z),new Point3d(east,0,z),"U_G",a,b,t,k)) return false;
            if(!MappedWidthWithinLimits(new Point3d(a[0],a[1],z),new Point3d(a[0],north,z),"U_G",a,b,t,k)) return false;
        }
        return true;
    }

    public static bool Legal(int s,double[] state)
    {
        if(s<0||s>1||state.Length!=3||state.Any(v=>double.IsNaN(v)||double.IsInfinity(v)||Math.Abs(v)>1))return false;
        if(!SpatialLimits(s,state))return false;
        var b=D(s,state);var z=new[]{0.0,b[10],b[10]+.30,b[11],b[11]+.30,b[12],b[13]+.18};
        if(z.Zip(z.Skip(1),(x,y)=>y-x).Any(v=>v<.10))return false;
        double c=b[0]*b[1]/(s==0?100:98),rw=-b[4],rd=b[1]-b[5],fw=b[0]-b[2],fd=-b[3];
        if(c<.90||c>1.10||Math.Max(rw,rd)/Math.Min(rw,rd)>2.10||Math.Max(rw,rd)/Math.Min(rw,rd)<1.40||Math.Max(fw,fd)/Math.Min(fw,fd)>2.70)return false;
        double k=VerticalScale(s,state);
        if(!((b[10]-.11)*k>=3 && (b[12]-.11)*k>=7 && (b[11]-b[10]-.35)*k>=1.8 && (b[13]-b[11]-.35)*k>=2.25))return false;
        return new[]{"EF_","FC_","RC_","GC_"}.All(p=>PortalWidth(s,state,p)>=2.02);
    }
    public static double[] Solve(int s,double[] previous,double[] requested,int axis)
    {
        var result=(double[])previous.Clone();double start=previous[axis],target=Math.Max(-1,Math.Min(1,requested[axis])),last=start;
        for(int step=1;step<=32;step++) {
            double next=start+(target-start)*step/32;result[axis]=next;
            if(!Legal(s,result)) {
                double lo=last,hi=next;
                for(int j=0;j<24;j++){double mid=(lo+hi)/2;result[axis]=mid;if(Legal(s,result))lo=mid;else hi=mid;}
                result[axis]=lo;return result;
            }last=next;
        }return result;
    }
}
