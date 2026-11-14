import assert from 'node:assert/strict';
import { loadModules, check, finish } from './check.mjs';
const [{ adaptiveBitrate }] = await loadModules('adaptive-bitrate.js');
check('congestion lowers bitrate promptly and waits before another change', () => {
 const a = adaptiveBitrate();
 assert.equal(a.sample({fps:30,deliverMs:150}),15000);
 assert.equal(a.sample({fps:30,deliverMs:150}),null);
 assert.equal(a.sample({fps:30,deliverMs:150}),null);
 assert.equal(a.sample({fps:30,deliverMs:150}),11000);
});
check('idle and invalid samples do not lower quality', () => {
 const a=adaptiveBitrate();
 for(const s of [{fps:1,deliverMs:500},{fps:30,deliverMs:NaN},{fps:30,deliverMs:0}]) assert.equal(a.sample(s),null);
});
check('recovery needs eight good samples and respects bounds', () => {
 const a=adaptiveBitrate(34000);
 for(let i=0;i<7;i++) assert.equal(a.sample({fps:60,deliverMs:10}),null);
 assert.equal(a.sample({fps:60,deliverMs:10}),35000);
 for(let i=0;i<100;i++) assert.equal(a.sample({fps:60,deliverMs:10}),null);
 const b=adaptiveBitrate(2000);
 for(let i=0;i<20;i++) assert.equal(b.sample({fps:30,deliverMs:500}),null);
});
finish('adaptive bitrate');
