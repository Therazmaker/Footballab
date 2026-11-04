import sys,math,collections
sys.argv=[sys.argv[0],sys.argv[1]]
exec(open(__import__('os').path.join(__import__('os').path.dirname(__file__),'backtest.py')).read().split("bylg=collections")[0])
bylg=collections.defaultdict(list)
for r in ms: bylg[r['lg']].append(r)
# residuos por equipo respecto al modelo 3 (lo que el modelo NO explica = la "firma")
res_t=collections.defaultdict(list)
for lg,rows in bylg.items():
    for i,r in enumerate(rows):
        hist=rows[:i]
        if len(hist)<60: continue
        att,de,home,tot=fit_dc(hist,r['t'],iters=25)
        if r['h'] not in att or r['a'] not in att: continue
        lh=att[r['h']]*de[r['a']]*tot*home; la=att[r['a']]*de[r['h']]*tot
        res_t[r['h']].append(dict(gf=r['hg']-lh,ga=r['ag']-la,draw=(r['hg']==r['ag'])-outcome(lh,la)[1],venue='H'))
        res_t[r['a']].append(dict(gf=r['ag']-la,ga=r['hg']-lh,draw=(r['hg']==r['ag'])-outcome(lh,la)[1],venue='A'))
def corr(x,y):
    n=len(x);mx=sum(x)/n;my=sum(y)/n
    sx=math.sqrt(sum((a-mx)**2 for a in x));sy=math.sqrt(sum((b-my)**2 for b in y))
    return sum((a-mx)*(b-my) for a,b in zip(x,y))/(sx*sy)
for key in ['gf','ga','draw']:
    A=[];B=[]
    for t,L in res_t.items():
        if len(L)<16: continue
        A.append(sum(x[key] for x in L[0::2])/len(L[0::2])); B.append(sum(x[key] for x in L[1::2])/len(L[1::2]))
    # tambien primera mitad vs segunda mitad (persistencia temporal)
    C=[];E=[]
    for t,L in res_t.items():
        if len(L)<16: continue
        h=len(L)//2; C.append(sum(x[key] for x in L[:h])/h); E.append(sum(x[key] for x in L[h:])/(len(L)-h))
    print(f"firma residual '{key}': equipos={len(A)}  pares/impares r={corr(A,B):+.2f}   1a mitad->2a mitad r={corr(C,E):+.2f}")
