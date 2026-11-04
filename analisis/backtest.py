import json,glob,math,sys,collections
from datetime import datetime
D=sys.argv[1]
ms=[]
seen=set()
for f in glob.glob(D+'/brain/database/tracker/tr_*.json'):
    m=json.load(open(f))
    try: hg,ag=int(m['homeGoals']),int(m['awayGoals'])
    except: continue
    k=(m['homeId'],m['awayId'],m['date'][:10])
    if k in seen: continue
    seen.add(k)
    ms.append(dict(lg=m['leagueId'],h=m['homeId'],a=m['awayId'],hg=hg,ag=ag,t=datetime.fromisoformat(m['date'][:16].replace(' ','T') if len(m['date'])>10 else m['date'])))
ms.sort(key=lambda r:r['t'])
print('partidos',len(ms))

def pois(k,l): return math.exp(-l)*l**k/math.factorial(k)
def outcome(lh,la,rho=0.0,G=10):
    H=Dr=A=0
    for i in range(G+1):
        for j in range(G+1):
            p=pois(i,lh)*pois(j,la)
            if rho:
                if i==0 and j==0: p*=1-lh*la*rho
                elif i==0 and j==1: p*=1+lh*rho
                elif i==1 and j==0: p*=1+la*rho
                elif i==1 and j==1: p*=1-rho
            if i>j:H+=p
            elif i==j:Dr+=p
            else:A+=p
    s=H+Dr+A; return (H/s,Dr/s,A/s)

def res(r): return 0 if r['hg']>r['ag'] else 1 if r['hg']==r['ag'] else 2
def rps(p,o):
    c=[p[0],p[0]+p[1]]; e=[1 if o==0 else 0, 1 if o<=1 else 0]
    return sum((c[i]-e[i])**2 for i in range(2))/2

MIN=6  # cada equipo con >= MIN partidos previos para evaluar
models=collections.defaultdict(lambda:[0,0,0,0])  # ll, rps, hits, n

def team_hist(hist,tid,before):
    return [r for r in hist if (r['h']==tid or r['a']==tid)]

def gf_ga(rows,tid,decay=None,now=None,venue=None):
    wsum=gf=ga=0
    for r in rows:
        if venue=='H' and r['h']!=tid: continue
        if venue=='A' and r['a']!=tid: continue
        w=1.0
        if decay: w=math.exp(-decay*(now-r['t']).days)
        if r['h']==tid: gf+=w*r['hg']; ga+=w*r['ag']
        else: gf+=w*r['ag']; ga+=w*r['hg']
        wsum+=w
    return (gf/wsum,ga/wsum,wsum) if wsum else (None,None,0)

def fit_dc(rows,now,xi=0.004,iters=40):
    # Poisson attack/defense con decaimiento temporal (Dixon-Coles 1997), ajuste iterativo + prior
    teams=set([r['h'] for r in rows]+[r['a'] for r in rows])
    att={t:1.0 for t in teams}; de={t:1.0 for t in teams}; home=1.2
    w=[math.exp(-xi*(now-r['t']).days) for r in rows]
    tot=sum(wi*(r['hg']+r['ag']) for wi,r in zip(w,rows))/ (2*sum(w))
    PRIOR=3.0  # partidos ficticios en la media (shrinkage bayesiano)
    for _ in range(iters):
        num=collections.defaultdict(float); den=collections.defaultdict(float)
        for wi,r in zip(w,rows):
            num[r['h']]+=wi*r['hg']; den[r['h']]+=wi*home*de[r['a']]*tot
            num[r['a']]+=wi*r['ag']; den[r['a']]+=wi*de[r['h']]*tot
        for t in teams: att[t]=(num[t]+PRIOR)/(den[t]+PRIOR)
        num=collections.defaultdict(float); den=collections.defaultdict(float)
        for wi,r in zip(w,rows):
            num[r['a']]+=wi*r['hg']; den[r['a']]+=wi*home*att[r['h']]*tot
            num[r['h']]+=wi*r['ag']; den[r['h']]+=wi*att[r['a']]*tot
        for t in teams: de[t]=(num[t]+PRIOR)/(den[t]+PRIOR)
        hn=sum(wi*r['hg'] for wi,r in zip(w,rows)); hd=sum(wi*att[r['h']]*de[r['a']]*tot for wi,r in zip(w,rows))
        home=hn/hd
    return att,de,home,tot

bylg=collections.defaultdict(list)
for r in ms: bylg[r['lg']].append(r)
for lg,rows in bylg.items():
    for i,r in enumerate(rows):
        hist=rows[:i]
        hh=team_hist(hist,r['h'],r['t']); ah=team_hist(hist,r['a'],r['t'])
        if len(hh)<MIN or len(ah)<MIN or len(hist)<60: continue
        o=res(r)
        lgavg=sum(x['hg']+x['ag'] for x in hist)/(2*len(hist))
        freq=[sum(1 for x in hist if res(x)==k)/len(hist) for k in range(3)]
        preds={}
        preds['0 baseline frecuencias liga']=tuple(freq)
        # 1) motor actual xg_engine.js: sin xG -> (gf+gf)/2 = gf ; multiplicación cruda
        hgf,hga,_=gf_ga(hh,r['h']); agf,aga,_=gf_ga(ah,r['a'])
        lh=max(0.2,hgf*aga*1.12); la=max(0.2,agf*hga)
        preds['1 xg_engine.js actual (todo el historial)']=outcome(lh,la)
        # 1b) lo mismo pero ultimos 5 (formLastN=5)
        hgf5,hga5,_=gf_ga(hh[-5:],r['h']); agf5,aga5,_=gf_ga(ah[-5:],r['a'])
        preds['1b xg_engine.js actual (ultimos 5)']=outcome(max(0.2,hgf5*aga5*1.12),max(0.2,agf5*hga5))
        # 2) normalizado por media de liga
        lh2=(hgf/lgavg)*(aga/lgavg)*lgavg*1.12; la2=(agf/lgavg)*(hga/lgavg)*lgavg
        preds['2 normalizado por media liga']=outcome(lh2,la2)
        # 3) Dixon-Coles con decay + shrinkage + rho
        att,de,home,tot=fit_dc(hist,r['t'])
        lh3=att[r['h']]*de[r['a']]*tot*home; la3=att[r['a']]*de[r['h']]*tot
        preds['3 Poisson MLE + decay + prior']=outcome(lh3,la3)
        preds['4 = 3 + correccion Dixon-Coles rho=-0.10']=outcome(lh3,la3,rho=-0.10)
        for k,p in preds.items():
            p=[max(1e-6,x) for x in p]
            m=models[k]; m[0]+=-math.log(p[o]); m[1]+=rps(p,o); m[2]+= (max(range(3),key=lambda j:p[j])==o); m[3]+=1
print(f"{'modelo':45s} {'n':>5s} {'logloss':>8s} {'RPS':>7s} {'acierto':>8s}")
for k in sorted(models):
    ll,rp,h,n=models[k]; print(f"{k:45s} {n:5d} {ll/n:8.4f} {rp/n:7.4f} {h/n:8.3f}")
