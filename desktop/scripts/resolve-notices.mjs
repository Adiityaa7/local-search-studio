import fs from 'node:fs';
import path from 'node:path';
const out=path.resolve(import.meta.dirname,'../notices');
const missing=JSON.parse(fs.readFileSync(path.join(out,'notice-gaps.json'),'utf8'));
const evidence=[];
for(let i=0;i<missing.length;i+=5) await Promise.all(missing.slice(i,i+5).map(async p=>{
 const vcs=JSON.parse(fs.readFileSync(path.join(p.dir,'.cargo_vcs_info.json'),'utf8'));
 const repo=p.repository.replace(/\.git$/,'').replace(/\/$/,'').replace('https://github.com/','');
 const base=`https://raw.githubusercontent.com/${repo}/${vcs.git.sha1}/`;
 const destination=path.join(out,'additional',p.name+'-'+p.version); fs.mkdirSync(destination,{recursive:true});
 const names=['LICENSE','LICENSE-MIT','LICENSE-APACHE','LICENSE.md','LICENSE.txt','LICENCE','COPYING','COPYRIGHT'];
 const found=[];
 await Promise.all(names.map(async name=>{try{const r=await fetch(base+name);if(r.ok){const text=await r.text();if(text.length>80&&!text.startsWith('<!DOCTYPE')){fs.writeFileSync(path.join(destination,name),text);found.push(name);evidence.push({package:p.name,version:p.version,file:name,url:base+name,revision:vcs.git.sha1});}}}catch(e){console.log(p.name+': '+e.message);}}));
 console.log(p.name+': '+found.join(', '));
}));
fs.writeFileSync(path.join(out,'notice-sources.json'),JSON.stringify(evidence,null,2));
