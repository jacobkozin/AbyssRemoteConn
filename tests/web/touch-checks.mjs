import assert from 'node:assert/strict';
import { loadModules, check, finish } from './check.mjs';
const [{stickAt,pinchZoom,neutralPad,shortcutSteps}] = await loadModules('touch-controls.js');
check('stick has dead zone and clamps diagonal travel to the unit circle',()=>{
 const rect={left:0,top:0,width:100,height:100};
 assert.deepEqual(stickAt(51,51,rect),{x:0,y:0});
 const p=stickAt(200,200,rect); assert.ok(Math.hypot(p.x,p.y)<=1.002);
 assert.deepEqual(stickAt(100,50,rect),{x:1,y:0});
});
check('pinch is proportional, bounded and ignores unusable geometry',()=>{
 assert.equal(pinchZoom(100,100,150),150);
 assert.equal(pinchZoom(100,100,500),250);
 assert.equal(pinchZoom(150,100,10),100);
 assert.equal(pinchZoom(150,0,100),150);
});
check('neutral state releases every axis and button',()=>{
 assert.ok(Object.values(neutralPad()).every(v=>v===0));
 const a=neutralPad(); a.buttons=1; assert.equal(neutralPad().buttons,0);
});
check('a shortcut presses its keys in order and releases them in reverse',()=>{
 assert.deepEqual(shortcutSteps('ControlLeft+ShiftLeft+Escape'),[
  {code:'ControlLeft',action:'down'},{code:'ShiftLeft',action:'down'},{code:'Escape',action:'down'},
  {code:'Escape',action:'up'},{code:'ShiftLeft',action:'up'},{code:'ControlLeft',action:'up'}]);
 assert.deepEqual(shortcutSteps('Tab'),[{code:'Tab',action:'down'},{code:'Tab',action:'up'}]);
 assert.deepEqual(shortcutSteps(''),[]);
});
finish('touch controls');
