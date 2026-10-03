#!/usr/bin/env python3
"""Summarise layer_info TSV: per-layer kind, tensor types, bytes. Usage: summarize.py model.tsv"""
import sys, re, collections
rows=[l.rstrip("\n").split("\t") for l in open(sys.argv[1]) if not l.startswith("#")]
L=collections.defaultdict(list); G=[]
for shard,layer,name,typ,*ne,b in rows:
    (L[int(layer)] if int(layer)>=0 else G).append((name,typ,int(b),ne))
GiB=1<<30
print("GLOBAL tensors (non-block):")
for n,t,b,ne in G: print(f"  {n:40s} {t:8s} {b/GiB:8.3f} GiB  ne={'x'.join(ne)}")
print(f"\n{'L':>2} {'kind':9s} {'gate':8s} {'up':8s} {'down':8s} {'exp GiB':>8} {'attn/ssm GiB':>12} {'shexp GiB':>9} {'other GiB':>9} {'total GiB':>9}  extra")
tot=collections.Counter()
for il in sorted(L):
    d={n:(t,b) for n,t,b,_ in L[il]}
    def ty(s): 
        k=f"blk.{il}.{s}.weight"; return d[k][0] if k in d else "-"
    kind="full-attn" if any(".attn_q." in n for n in d) else ("DeltaNet" if any("ssm_" in n or "attn_qkv" in n for n in d) else "?")
    nextn=any("nextn" in n for n in d)
    exp=sum(b for n,(t,b) in d.items() if re.search(r"ffn_(gate|up|down)_exps",n))
    shx=sum(b for n,(t,b) in d.items() if "_shexp" in n)
    attn=sum(b for n,(t,b) in d.items() if re.search(r"attn|ssm",n))
    allb=sum(b for t,b in d.values()); oth=allb-exp-shx-attn
    for k,v in (("exp",exp),("shexp",shx),("attn",attn),("other",oth)): tot[k]+=v
    ex="nextn/MTP" if nextn else ""
    print(f"{il:>2} {kind:9s} {ty('ffn_gate_exps'):8s} {ty('ffn_up_exps'):8s} {ty('ffn_down_exps'):8s} {exp/GiB:8.3f} {attn/GiB:12.3f} {shx/GiB:9.3f} {oth/GiB:9.3f} {allb/GiB:9.3f}  {ex}")
print(f"\nTOTALS GiB: experts {tot['exp']/GiB:.2f}  attn/ssm {tot['attn']/GiB:.2f}  shared-exp {tot['shexp']/GiB:.2f}  other-in-block {tot['other']/GiB:.2f}  non-block {sum(b for _,_,b,_ in G)/GiB:.2f}  ALL {(sum(tot.values())+sum(b for _,_,b,_ in G))/GiB:.2f}")
c=collections.Counter()
for il in L:
    d={n:t for n,t,_,_ in L[il]}
    c[(d.get(f"blk.{il}.ffn_gate_exps.weight","-"),d.get(f"blk.{il}.ffn_up_exps.weight","-"),d.get(f"blk.{il}.ffn_down_exps.weight","-"))]+=1
print("\nexpert quant combos (gate,up,down) -> #layers:"); [print(" ",k,v) for k,v in c.most_common()]
