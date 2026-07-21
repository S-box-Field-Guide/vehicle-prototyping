"""Offline landing-corridor + overlap audit for PlaygroundBuilder.BuildProtoStuntZones.

Ports RampKicker's LengthFor / RadiusFor / EasementCore / FlightRangeM (floor-90 law,
easement blend 0.5, 1.1 g scene gravity) so the stunt-park layout can be checked WITHOUT the
editor. For every kicker it computes the base->lip footprint and the ballistic landing corridor
at full-bore 46 m/s, then flags: corridor crossing a collidable obstacle (banked-curve wall,
external hill ramps, another feature), corridor/body landing into another kicker's body, a
full-bore landing leaving the drivable slab, and any footprint outside its zone rectangle.
Same-mound faces (shared crest, rollable by design) and set-piece internal landings
(jump-onto-box, tabletop deck) are expected and read as flags for the SW set-pieces only.

Run: python tools/layout_validate.py   (expect 0 issues for NORTH and SE zones).
Keep this in sync with any layout edit; it is the source of the audit in the C# doc comment.
"""
import math

MIN_R=90.0; A_CAP=12.9; B_MARGIN=1.1; BLEND=0.5; G=9.81*1.1
V_FULL=46.0   # full-bore safety arrival
V_TYP=30.0
SLAB=(500.0,1500.0,-280.0,320.0)   # track hardpack extent; corridors must land inside

def radius_for(h,vdes=0.0):
    r=max(MIN_R,52*h)
    if vdes>0: r=max(r,B_MARGIN*vdes*vdes/A_CAP)
    return r
def length_for(h,vdes=0.0):
    r=radius_for(h,vdes); return max(5*h, math.sqrt(h*(2*r-h)))
def ease(L,H,segs=16):
    R=(L*L+H*H)/(2*H); th=math.asin(min(max(L/R,0),1)); S=th*R/(1-BLEND*0.5)
    n=segs*64; ds=S/n; sB=BLEND*S; x=z=t=0.0
    for i in range(1,n+1):
        sM=(i-0.5)*ds; k=(sM/sB)/R if sM<sB else 1/R
        t+=k*ds; x+=math.cos(t-k*ds*0.5)*ds; z+=math.sin(t-k*ds*0.5)*ds
    sc=H/z; return x*sc, math.degrees(t)
def run_of(h,vdes=0.0): return ease(length_for(h,vdes),h)[0]
def exit_of(h,vdes=0.0): return ease(length_for(h,vdes),h)[1]
def frange(h,v,vdes=0.0):
    e=math.radians(exit_of(h,vdes)); vx=v*math.cos(e); vz=v*math.sin(e)
    tt=(vz+math.sqrt(vz*vz+2*G*h))/G; return vx*tt

# ---- feature footprints as axis-aligned rects (x0,x1,y0,y1) for obstruction tests ----
# Only CAR-MEETABLE tall obstructions matter (vertical faces / ring / decks).
obstacles=[]  # (name, x0,x1,y0,y1)
def rect(name,cx,cy,sx,sy): obstacles.append((name,cx-sx/2,cx+sx/2,cy-sy/2,cy+sy/2))

# kickers: (name, bx,by, yawdeg, h, w, vdes)
kickers=[]
def K(name,bx,by,yaw,h,w=8,vdes=0.0): kickers.append((name,bx,by,yaw,h,w,vdes))

# ---------------- LAYOUT UNDER TEST (world coords) ----------------
def load_layout(spec):
    obstacles.clear(); kickers.clear(); spec()

def seg_rect_hit(p0,p1,w,rc):
    # does the swept segment p0->p1 (width w) intersect rect rc? sample points along seg, test AABB inflated by w/2
    x0,x1,y0,y1=rc[1],rc[2],rc[3],rc[4]
    n=40
    for i in range(n+1):
        t=i/n; x=p0[0]+(p1[0]-p0[0])*t; y=p0[1]+(p1[1]-p0[1])*t
        if x0-w/2<=x<=x1+w/2 and y0-w/2<=y<=y1+w/2: return True
    return False

def analyze(zone_rects=None, report=True):
    issues=[]
    info={}
    for (name,bx,by,yaw,h,w,vd) in kickers:
        r=run_of(h,vd); e=math.radians(yaw)
        lip=(bx+r*math.cos(e), by+r*math.sin(e))
        rf=frange(h,V_FULL,vd); rt=frange(h,V_TYP,vd)
        land_full=(lip[0]+rf*math.cos(e), lip[1]+rf*math.sin(e))
        land_typ=(lip[0]+rt*math.cos(e), lip[1]+rt*math.sin(e))
        info[name]=dict(base=(bx,by),lip=lip,run=r,exit=exit_of(h,vd),
                        land_typ=land_typ,land_full=land_full,rf=rf,h=h,w=w)
        for rc in obstacles:
            if seg_rect_hit(lip,land_full,w,rc):
                issues.append(f"CORRIDOR {name} (h{h}) crosses obstacle {rc[0]}")
            # kicker BODY (base->lip) must not sit in an obstacle / keep-clear rect
            if seg_rect_hit((bx,by),lip,w,rc):
                issues.append(f"BODY {name} (h{h}) overlaps obstacle {rc[0]}")
        # full-bore landing must stay on the drivable slab
        lx,ly=land_full
        if not (SLAB[0]<=lx<=SLAB[1] and SLAB[2]<=ly<=SLAB[3]):
            issues.append(f"LANDING {name}(h{h}) full-bore lands OFF SLAB ({lx:.0f},{ly:.0f})")
    def kfoot(k):
        name,bx,by,yaw,h,w,vd=k; r=run_of(h,vd); e=math.radians(yaw)
        lip=(bx+r*math.cos(e),by+r*math.sin(e))
        return (min(bx,lip[0])-w/2,max(bx,lip[0])+w/2,min(by,lip[1])-w/2,max(by,lip[1])+w/2),lip,e,w,h,vd
    foots=[(k[0],)+kfoot(k) for k in kickers]
    for i,(ni,fi,lipi,ei,wi,hi,vdi) in enumerate(foots):
        rf=frange(hi,V_FULL,vdi)
        land=(lipi[0]+rf*math.cos(ei), lipi[1]+rf*math.sin(ei))
        for j,(nj,fj,lipj,ej,wj,hj,vdj) in enumerate(foots):
            if i==j: continue
            # skip the same double-mound's other face (shared crest, rollable by design)
            if ni.rsplit('_',1)[0]==nj.rsplit('_',1)[0] and ni[-2:] in('_W','_E') and nj[-2:] in('_W','_E'): continue
            rc=("K:"+nj,fj[0],fj[1],fj[2],fj[3])
            if seg_rect_hit(lipi,land,wi,rc):
                issues.append(f"CORRIDOR {ni}(h{hi}) lands into kicker {nj} body")
    # zone containment of kicker footprints
    if zone_rects:
        for k in kickers:
            f,lip,e,w,h,vd=kfoot(k)
            inside=any(zx0<=f[0] and f[1]<=zx1 and zy0<=f[2] and f[3]<=zy1 for(zn,zx0,zx1,zy0,zy1) in zone_rects)
            if not inside:
                issues.append(f"FOOTPRINT {k[0]} outside zones  x[{f[0]:.0f},{f[1]:.0f}] y[{f[2]:.0f},{f[3]:.0f}]")
    if report:
        print(f"kickers={len(kickers)} obstacles={len(obstacles)}  issues={len(issues)}")
        for s in issues: print("  !",s)
        print("  --- kicker corridors (lip -> full-bore land) ---")
        for n,d in info.items():
            print(f"  {n:<16} h{d['h']} exit{d['exit']:.1f} lip({d['lip'][0]:.0f},{d['lip'][1]:.0f}) "
                  f"landFULL({d['land_full'][0]:.0f},{d['land_full'][1]:.0f}) range46={d['rf']:.0f}")
    return issues

def DM(name,cx,cy,h,w=7):
    r=run_of(h)
    K(name+"_W",cx-r,cy,0,h,w)      # west face launches +X
    K(name+"_E",cx+r,cy,180,h,w)    # east face launches -X

def ladder(name,bx,by):
    for i,h in enumerate([0.6,1.0,2.0,3.0,4.5]):
        K(f"{name}{i}",bx,by-48+i*24,0,h,8)

# obstacle: model my own big-air chain footprint + external banked curve
def north_band():
    rect("BankedCurve",1277,197,52,60)           # collidable arc wall, NE corner (x1251-1303,y167-227)
    rect("BigAirChain",870,300,240,18)           # my big-air set-piece strip along y300 (x750-990)
    ladder("Lad",735,132)                          # calibrated lane, tucked W edge, faces +X
    # --- multi-directional scatter (clusters + fans + bidirectional mounds) ---
    # west-center fan (angular coverage N / NE / E)
    K("fanA_N",905,175,90,1.0)
    K("fanA_NE",915,150,55,1.2)
    K("fanA_E",905,120,10,1.0)
    # bidirectional mounds (each gives two opposite headings)
    DM("dmMid",1050,120,1.5)
    DM("dmHi",1080,235,1.2)
    # east fan (S-of-curve headings E / SE / NW)
    K("fanB_E",1150,150,0,1.0)
    K("fanB_SE",1160,120,315,1.0)
    K("fanB_NW",1140,175,105,1.2)
    # north-edge low pop facing N (kept low so it lands short of slab edge)
    K("nEdge",1165,225,90,0.6)
    # south pop landing on the open drag flat
    K("sPop",1000,108,270,1.0)
    # extra coverage singles in open pockets
    K("scNE",1210,265,180,1.0)    # NE corner, faces W back across the field
    K("scN2",980,272,315,0.6)     # N-centre, small SE pop
    K("scSE",1205,92,0,1.0)       # SE of band, faces E (open, S of banked curve)

NB_ZONE=[("north",720,1240,80,318)]

def se_zone():
    # KEPT features as obstacles/keep-clear:
    rect("Bowl",1150,-200,74,74)                 # banked ring wall (r34 +overlap)
    rect("BowlMouthLane",1200,-150,60,60)        # NE drive-in lane to the bowl mouth: keep clear
    rect("WallRide",1240,-120,40,33)             # quarter-pipe + vertical x-end faces
    rect("HillRamps",1040,-205,28,120)           # external proving-ground hill ladder (collidable)
    # NEW smooth multi-directional features where the hard stairs were (x~1100,y-95):
    DM("seEntry",1100,-108,1.2)                  # bidirectional E-W mound where the stairs were
    K("seE",1280,-110,90,1.0)                    # N pop in the open pocket E of the wall-ride

SE_ZONE=[("se",1060,1300,-278,-85)]

def tabletop(name,cx,cy,deckTop=1.4,deckHalf=7,w=9):
    run=run_of(deckTop)
    K(name+"_up",cx-deckHalf-run,cy,0,deckTop,w)
    rect(name+"Deck",cx,cy,deckHalf*2,w)
    K(name+"_dn",cx+deckHalf+run,cy,180,deckTop,w)

def jumpbox(name,bx,by,launchH=3.0,boxTop=3.2,boxDepth=15,boxW=12):
    run=run_of(launchH); lipX=bx+run; boxFrontX=lipX+12; boxBackX=boxFrontX+boxDepth
    K(name+"_launch",bx,by,0,launchH,boxW)
    rect(name+"Box",(boxFrontX+boxBackX)/2,by,boxDepth,boxW)
    dnrun=run_of(boxTop)
    K(name+"_down",boxBackX+dnrun,by,180,boxTop,boxW)

def sw_zone():
    # welcome zone off the spur: KEEP jumpbox, tabletop, mounds (owner). Re-verified under floor-90.
    jumpbox("jb",505,-110)
    tabletop("tt",560,-180)
    DM("m1",540,-140,1.0)
    DM("m2",600,-230,1.2)

SW_ZONE=[("sw",505,625,-270,-75)]
if __name__=="__main__":
    print("=== NORTH BAND ==="); load_layout(north_band); analyze(NB_ZONE)
    print("\n=== SE ZONE ==="); load_layout(se_zone); analyze(SE_ZONE)
    print("\n=== SW ZONE ==="); load_layout(sw_zone); analyze(SW_ZONE)
