"""Generates src/Lasteast.Core/Localization/Strings/{lang}.json from the aligned tables in tools/strings_part*.py."""
import json, importlib.util, pathlib
langs = ["en","he","ar","fr","es","de","ru"]
root = pathlib.Path(__file__).parent
table = {}
for i in range(1,13):
    spec = importlib.util.spec_from_file_location(f"p{i}", root/f"strings_part{i}.py"); m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
    for k,v in m.S.items():
        assert k not in table, k; table[k]=v
    if hasattr(m,"ROLES"):
        for k,v in m.ROLES.items(): table["Role."+k]=v
        for k,v in m.BUS.items(): table["Bus."+k]=v
    for k,v in getattr(m,"OVERRIDE",{}).items():
        assert k in table, k; table[k]=v
for k,v in table.items(): assert len(v)==7, k
out = root.parent/"src/Lasteast.Core/Localization/Strings"
for i,l in enumerate(langs):
    (out/f"{l}.json").write_text(json.dumps({k:table[k][i] for k in sorted(table)}, ensure_ascii=False, indent=2), encoding="utf-8")
print(len(table), "keys x", len(langs), "languages")
