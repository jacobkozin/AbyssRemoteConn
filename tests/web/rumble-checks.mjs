import assert from 'node:assert/strict';
import { loadModules, check, finish } from './check.mjs';
const [{rumbleValues,controllerRumble}] = await loadModules('rumble.js');
check('feedback indices and strengths are bounded',()=>{
 assert.equal(rumbleValues({index:4,strong:1}),null);
 assert.deepEqual(rumbleValues({index:0,strong:Infinity,weak:2}),{index:0,strong:0,weak:1});
});
check('feedback reaches only its controller and reset stops it',()=>{
 const effects=[],stops=[];
 const pads=()=>[0,1].map(index=>({index,connected:true,vibrationActuator:{
  playEffect:(...args)=>effects.push([index,...args]),reset:()=>stops.push(index)}}));
 let allowed=true; const r=controllerRumble(()=>allowed,pads);
 r.receive({index:1,strong:0.5,weak:0.25});
 assert.equal(effects.length,1);assert.equal(effects[0][0],1);
 r.reset();assert.deepEqual(stops,[1]);
 allowed=false;r.receive({index:0,strong:1,weak:1});assert.equal(effects.length,1);
});
finish('controller feedback');
