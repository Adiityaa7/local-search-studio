import fs from 'node:fs';
import path from 'node:path';
const metadata=JSON.parse(fs.readFileSync(process.argv[2], 'utf8').replace(/^\uFEFF/,''));
const nodes=new Map(metadata.resolve.nodes.map(n=>[n.id,n]));
const packages=new Map(metadata.packages.map(p=>[p.id,p]));
const start=metadata.packages.find(p=>p.name==='xerj-server').id;
const visited=new Set();
function visit(id){if(visited.has(id))return;visited.add(id);for(const dep of nodes.get(id)?.deps??[])if(dep.dep_kinds.some(k=>k.kind!=='dev'))visit(dep.pkg);}
visit(start);
function find(dir,level=0){if(level>7)return [];let result=[];for(const e of fs.readdirSync(dir,{withFileTypes:true})){if(e.isSymbolicLink())continue;const p=path.join(dir,e.name);if(e.isFile()&&/^(licen[sc]e|copying|copyright|notice)([._-]|$)/i.test(e.name)&&fs.statSync(p).size<500000)result.push(p);else if(e.isDirectory()&&!['.git','target','tests','test','benches','examples'].includes(e.name))result.push(...find(p,level+1));}return result;}
const missing=[], inventory=[], sections=[];
for(const id of [...visited].sort()){
 const p=packages.get(id); if(!p.source)continue;
 const dir=path.dirname(p.manifest_path), files=find(dir);
 if(p.license_file&&fs.existsSync(path.resolve(dir,p.license_file)))files.push(path.resolve(dir,p.license_file));
 const extra=path.resolve(import.meta.dirname,'../notices/additional',p.name+'-'+p.version);
 if(fs.existsSync(extra))files.push(...find(extra));
 const unique=[...new Set(files)]; const label=f=>f.startsWith(extra)?"additional/"+p.name+"-"+p.version+"/"+path.relative(extra,f):path.relative(dir,f);
 inventory.push({name:p.name,version:p.version,license:p.license,repository:p.repository,files:unique.map(label)});
 if(!unique.length)missing.push({name:p.name,version:p.version,license:p.license,repository:p.repository,dir});
 sections.push('\n'+'='.repeat(80)+'\n'+p.name+' '+p.version+'\nLicense: '+p.license+'\nSource: '+(p.repository??p.source)+'\n'+unique.map(f=>'\n--- '+label(f)+' ---\n'+fs.readFileSync(f,'utf8')).join('\n'));
}
const out=path.resolve(import.meta.dirname,'../notices');
fs.writeFileSync(path.join(out,'THIRD-PARTY-NOTICES.txt'),'Local Search Studio — third-party notices\nXERJ v1.0.0-rc.95; Windows x64 dependency graph, including build dependencies.\nIndividual components retain their original licenses.\n'+sections.join('\n'));
fs.writeFileSync(path.join(out,'dependency-inventory.json'),JSON.stringify(inventory,null,2));
fs.writeFileSync(path.join(out,'notice-gaps.json'),JSON.stringify(missing,null,2));
console.log(JSON.stringify({packages:inventory.length,missing},null,2));

