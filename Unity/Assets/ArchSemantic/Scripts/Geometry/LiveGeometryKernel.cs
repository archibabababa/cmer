using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ArchSemanticGeometry;

namespace ArchSemanticPilot
{
    public static class LiveGeometryKernel
    {
        public const string SourceGhSha256 = "644478046d68d5c70cb8ecf9e8432c29b1310a7c8f990845683dded2ed9791cf";
        public sealed class Result
        {
            // All solid geometry, including walls, slabs, doors, windows, lighting and displays.
            public List<GeometryBase> solids;
            // Semantic names corresponding one-to-one with solids: Scenario__Class__Owner__Role__Index.
            public List<string> names;
            // Anchors and start points for navigation, prompts and interaction.
            public List<Point3d> anchors, starts;
            public List<string> anchorNames;
            // Default viewing direction lines for the main spaces.
            public List<LineCurve> views;
            // Effective parameter values recorded in the output.
            public double p1, p2, p3, groundFloorHeight;
            // Actual shared-portal clear widths after taper and their semantic owners.
            public double[] portalWidths;
            public string[] portalOwners;
            // Target volume for outer-layer volume-contract validation.
            public double nominalVolume;
            // Projection or constraint description; usually NONE here because GeometryDomain handles constraints.
            public string projection;
            public int scenario;
            public double[] cageState;
        }

        sealed class Part
        {
            public string name, source;
            public Point3d[] vertices, corners;
            public int[] triangles;
        }
        static readonly Part[][] models = new Part[2][];
        static readonly Point3d[][] starts = new Point3d[2][];
        static readonly object gate = new object();

        public static void ReloadResources() { lock (gate) { models[0]=null; models[1]=null; } LoadResources(); }

        public static void LoadResources()
        {
            for (int i = 0; i < 2; i++)
                if (models[i] == null)
                {
                    string path = "CMER/" + (i == 0 ? "A" : "B") + "/";
                    var protocol = UnityEngine.Resources.Load<UnityEngine.TextAsset>(path + "Protocol");
                    if (protocol == null) throw new InvalidDataException("Missing CMER GH data: " + path);
                    Load(i, null, protocol.text);
                }
        }
        public static void Load(int scenario, string restJson, string protocolJson)
        {
            var protocol = (Dictionary<string, object>)DataJson.Read(protocolJson);
            var rest = restJson == null ? (Dictionary<string, object>)protocol["restModel"] : (Dictionary<string, object>)DataJson.Read(restJson);
            if ((string)rest["schema"] != "archsemantic-baseline/1" || (string)protocol["schema"] != "cmer-cage-model/1" ||
                (string)rest["scenario"] != (scenario == 0 ? "A" : "B") || (string)rest["units"] != "metres")
                throw new InvalidDataException("Incompatible CMER model protocol.");
            var mapping = (Dictionary<string, object>)protocol["mapping"];
            if ((string)mapping["algorithm"] != "cmer-cage-20261118/4") throw new InvalidDataException("Unsupported cage rules.");
            var cages = ((List<object>)mapping["cages"]).Cast<Dictionary<string, object>>()
                .ToDictionary(c => (string)c["sourceName"], c => Points(c["referenceCorners"]));
            var parts = ((List<object>)rest["objects"]).Cast<Dictionary<string, object>>().Select(o => new Part {
                name = (string)o["id"], source = (string)o["sourceName"], vertices = Points(o["vertices"]),
                corners = cages[(string)o["sourceName"]],
                triangles = ((List<object>)o["triangles"]).Select(x => checked((int)(double)x)).ToArray()
            }).ToArray();
            if (parts.Length == 0 || parts.Select(p => p.name).Distinct().Count() != parts.Length ||
                parts.Any(p => p.corners.Length != 8 || p.triangles.Length % 3 != 0 || p.triangles.Any(v => v < 0 || v >= p.vertices.Length)))
                throw new InvalidDataException("Invalid cage topology.");
            lock (gate) { starts[scenario] = Points(rest["starts"]); models[scenario] = parts; }
        }
        static Point3d[] Points(object value) => ((List<object>)value).Cast<List<object>>()
            .Select(v => new Point3d((double)v[0], (double)v[2], (double)v[1])).ToArray();

        // GH Box Morph: interpolate transformed cage corners in the rest box.
        static Point3d Morph(Point3d p, Point3d[] a, Point3d[] b)
        {
            var ab = a[1] - a[0]; var ad = a[3] - a[0]; var ae = a[4] - a[0]; var d = p - a[0];
            double u = Dot(d, ab) / Dot(ab, ab), v = Dot(d, ad) / Dot(ad, ad), w = Dot(d, ae) / Dot(ae, ae);
            double[] weights = {(1-u)*(1-v)*(1-w),u*(1-v)*(1-w),u*v*(1-w),(1-u)*v*(1-w),
                (1-u)*(1-v)*w,u*(1-v)*w,u*v*w,(1-u)*v*w};
            return new Point3d(Enumerable.Range(0,8).Sum(i=>weights[i]*b[i].X),
                Enumerable.Range(0,8).Sum(i=>weights[i]*b[i].Y),Enumerable.Range(0,8).Sum(i=>weights[i]*b[i].Z));
        }
        static double Dot(Vector3d a, Vector3d b) => a.X*b.X+a.Y*b.Y+a.Z*b.Z;

        public static Result Evaluate(int scenario, double p1, double p2, double p3)
        {
            var state = new[] {p1,p2,p3};
            if (!CageRules.Legal(scenario, state)) throw new InvalidDataException("State exceeds the GH spatial limits.");
            Part[] parts; lock (gate) parts = models[scenario];
            if (parts == null) { LoadResources(); lock (gate) parts = models[scenario]; }
            var r = new Result {scenario=scenario, cageState=state,p1=p1,p2=p2,p3=p3,
                solids=new List<GeometryBase>(),names=new List<string>(),starts=new List<Point3d>(),
                anchors=new List<Point3d>(),anchorNames=new List<string>(),views=new List<LineCurve>(),
                nominalVolume=CageRules.TargetVolume(scenario),projection="NONE",
                portalWidths=new[]{"EF_","FC_","RC_","GC_"}.Select(k=>CageRules.PortalWidth(scenario,state,k)).ToArray(),
                portalOwners=new[]{"E-F","F-C","R-C","G-C"}};
            var a=CageRules.Dimensions(scenario,0,0,0);var b=CageRules.D(scenario,state);
            var q=CageRules.Q(state);double k=CageRules.VerticalScale(scenario,state), taper=CageRules.Taper(q);
            foreach (var part in parts)
            {
                var center = new Point3d(part.corners.Average(p=>p.X),part.corners.Average(p=>p.Y),part.corners.Average(p=>p.Z));
                var tag=part.source.Split(new[]{"__"},StringSplitOptions.None);
                Func<Point3d,Point3d> map=p=>CageRules.Map(CageRules.Local(p,tag[3],a,q),tag[2],a,b,taper,k);
                Point3d[] target;
                if(tag[3]=="DESK"||tag[3]=="BENCH"||tag[3]=="PLINTH") {
                    center.Z=0;var mapped=map(center);target=part.corners.Select(p=>mapped+(p-center)).ToArray();
                } else target=part.corners.Select(map).ToArray();
                var solid = new Solid();
                foreach (var p in part.vertices) solid.Vertices.Add(new Vertex(Morph(p,part.corners,target)));
                for (int t=0;t<part.triangles.Length;t+=3) solid.Faces.Add(new[]{part.triangles[t],part.triangles[t+2],part.triangles[t+1]});
                r.solids.Add(solid);r.names.Add(part.name);
            }
            r.groundFloorHeight=CageRules.Map(new Point3d(0,0,.06),"F",a,b,taper,k).Z;
            for (int i=0;i<3;i++) {
                var p=starts[scenario][i];p.Z=0;
                p=CageRules.Map(p,new[]{"F","R","G"}[i],a,b,taper,k);p.Z=r.groundFloorHeight+1.65;
                r.starts.Add(p);r.views.Add(new LineCurve(p,new Point3d(b[0]/2,b[1]/2,p.Z)));
            }
            for (int i=0;i<9;i++) {r.anchors.Add(r.starts[i%3]);r.anchorNames.Add("ANCHOR_"+i);}
            var metrics=new GeometryMetrics{rooms=MeasureRooms(r)};
            RouteValidation.PlaceStarts(r,metrics);
            return r;
        }
        public static RoomMetrics[] MeasureRooms(Result r)
        {
            var a=CageRules.Dimensions(r.scenario,0,0,0);var b=CageRules.D(r.scenario,r.cageState);
            double t=CageRules.Taper(CageRules.Q(r.cageState)),k=CageRules.VerticalScale(r.scenario,r.cageState);
            double[][] plans={new[]{a[2],a[3],a[0],a[3],a[0],0, a[2],0},
                new[]{a[4],a[5],0,a[5],0,a[1],a[4],a[1]},
                new[]{a[6],a[1],a[0],a[1],a[0],a[8],a[7],a[8],a[7],a[9],a[6],a[9]},
                new[]{0.0,0,a[0],0,a[0],a[1],0,a[1]}};
            string[] owners={"F","R","G","C"};double[] heights={b[12],b[10],b[11],0};
            return plans.Select((plan,i)=>{
                var pp=Enumerable.Range(0,plan.Length/2).Select(j=>CageRules.Map(new Point3d(plan[2*j],plan[2*j+1],0),owners[i],a,b,t,k)).ToArray();
                double area=Math.Abs(Enumerable.Range(0,pp.Length).Sum(j=>pp[j].X*pp[(j+1)%pp.Length].Y-pp[(j+1)%pp.Length].X*pp[j].Y)/2);
                double h=heights[i], scale=1+t*h/(b[13]+.18);
                return new RoomMetrics{room=owners[i],area=area,height=h*k,volume=area*h*k*(1+scale+scale*scale)/3,
                    footprintXY=pp.SelectMany(p=>new[]{p.X,p.Y}).ToArray()};
            }).ToArray();
        }
    }

    // Small JSON reader for GH arrays, objects, numbers and strings.
    internal sealed class DataJson
    {
        readonly string text;int pos;
        DataJson(string s){text=s;}
        public static object Read(string s){var p=new DataJson(s);var v=p.Value();p.Space();if(p.pos!=s.Length)throw new FormatException("Trailing JSON data.");return v;}
        void Space(){while(pos<text.Length&&char.IsWhiteSpace(text[pos]))pos++;}
        char Take(){if(pos>=text.Length)throw new FormatException("Incomplete JSON.");return text[pos++];}
        object Value(){Space();char c=Take();
            if(c=='{'){var d=new Dictionary<string,object>();Space();if(text[pos]=='}'){pos++;return d;}
                do{Space();if(Take()!='"')throw new FormatException("JSON key expected.");string key=String();Space();if(Take()!=':')throw new FormatException();d.Add(key,Value());Space();c=Take();}while(c==',');if(c!='}')throw new FormatException();return d;}
            if(c=='['){var a=new List<object>();Space();if(text[pos]==']'){pos++;return a;}do{a.Add(Value());Space();c=Take();}while(c==',');if(c!=']')throw new FormatException();return a;}
            if(c=='"')return String();
            int start=pos-1;while(pos<text.Length&&",]} \t\r\n".IndexOf(text[pos])<0)pos++;string token=text.Substring(start,pos-start);
            if(token=="true")return true;if(token=="false")return false;if(token=="null")return null;
            double n=double.Parse(token,CultureInfo.InvariantCulture);if(double.IsNaN(n)||double.IsInfinity(n))throw new FormatException();return n;}
        string String(){var b=new StringBuilder();while(true){char c=Take();if(c=='"')return b.ToString();if(c=='\\'){c=Take();
                if(c=='u'){b.Append((char)int.Parse(text.Substring(pos,4),NumberStyles.HexNumber));pos+=4;continue;}
                int i="\"\\/bfnrt".IndexOf(c);if(i<0)throw new FormatException();c="\"\\/\b\f\n\r\t"[i];}b.Append(c);}}
    }
    public static class PracticeGeometry
    {
        public static LiveGeometryKernel.Result Evaluate(SpatialState p)
        {
            var r = new LiveGeometryKernel.Result
            {
                solids = new List<GeometryBase>(),
                names = new List<string>(),
                starts = new List<Point3d>(),
                views = new List<LineCurve>(),
                anchors = new List<Point3d>(),
                anchorNames = new List<string>(),
                p1 = p.p1,
                p2 = p.p2,
                p3 = p.p3,
                groundFloorHeight = .06,
                projection = "PRACTICE",
                portalWidths = new[]
                {
                    2.2
                },
                portalOwners = new[]
                {
                    "PRACTICE"
                }
            };
            for (int room = 0; room < 3; room++)
            {
                // Apply lightweight parameter deformation to each practice room while retaining floors, walls, roofs and observation points.
                string owner = new[]
                {
                    "F",
                    "R",
                    "G"
                }[room];
                double x = room * 7, w = 5 + .3 * p[room], d = 5 - .2 * p[(room + 1) % 3], h = 3.6 + .2 * p[room];
                Add(r, owner, "SLB", "OWNED_FOUNDATION", x, 0, w, d, -.3, .3);
                Add(r, owner, "FLR", "GROUND_FINISH", x, 0, w, d, 0, .06);
                Add(r, owner, "WAL", "LEFT", x, 0, .2, d, 0, h);
                Add(r, owner, "WAL", "RIGHT", x + w - .2, 0, .2, d, 0, h);
                Add(r, owner, "WAL", "BACK", x, d - .2, w, .2, 0, h);
                string roof = room == 0 ? "VARIABLE_F_ROOF_WITH_SKYLIGHT" : room == 1 ? "ROOF_AND_U_R1_VISIBLE_STRUCTURE" : "ROOF_AND_U_G_VISIBLE_STRUCTURE";
                Add(r, owner, "SLB", roof, x, 0, w, d, h, .18);
                var start = new Point3d(x + w * .5, 1.2, 1.71);
                r.starts.Add(start);
                r.views.Add(new LineCurve(start, start + new Vector3d(0, -3, 0)));
                r.anchors.Add(start);
                r.anchorNames.Add("PRACTICE_" + owner);
                r.nominalVolume += w * d * h;
            }

            for (int i = 0; i < 6; i++)
            {
                r.anchors.Add(r.starts[i % 3]);
                r.anchorNames.Add("PRACTICE_CUE_" + i);
            }

            return r;
        }
        static void Add(LiveGeometryKernel.Result r, string owner, string cat, string role, double x, double y, double w, double d, double z, double h)
        {
            // Practice components retain the formal naming convention but use scenario P (Practice).
            r.names.Add("P__" + cat + "__" + owner + "__" + role + "__01");
            r.solids.Add(Solid.Prism(new[] { new Point3d(x, y, z), new Point3d(x + w, y, z), new Point3d(x + w, y + d, z), new Point3d(x, y + d, z) }, h));
        }
    }
}

