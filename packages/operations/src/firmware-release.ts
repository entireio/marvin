import {openSync,fstatSync,readSync,closeSync} from 'node:fs';
import {isAbsolute,join} from 'node:path';
import {createHash,createPublicKey,verify} from 'node:crypto';
import {z} from 'zod';
/** Operator-selected, immutable in-memory release. No private signing key or
 * upload endpoint exists on the application server. Devices verify independently. */
function boundedFile(path:string,max:number){const fd=openSync(path,'r');try{const stat=fstatSync(fd);if(!stat.isFile()||stat.size>max||stat.size<1)throw new Error('Invalid firmware release file');const data=Buffer.alloc(stat.size);let n=0;while(n<data.length){const read=readSync(fd,data,n,data.length-n,null);if(!read)throw new Error('Truncated firmware release file');n+=read;}return data;}finally{closeSync(fd);}}
function field(data:Buffer){const end=data.indexOf(0);if(end<=0||data.subarray(0,end).some(x=>x<32||x>126)||data.subarray(end).some(x=>x!==0))throw new Error('Invalid firmware manifest field');return data.subarray(0,end).toString('ascii');}
export class FirmwareRelease {
 readonly sequence:number;readonly minimumSequence:number;readonly layout:string;
 private devices:Set<string>;private manifest:Buffer;private image:Buffer;
 constructor(configFile:string){
  const options=z.object({publicKeyFile:z.string().refine(isAbsolute),bundleDirectory:z.string().refine(isAbsolute),deviceIds:z.array(z.string().regex(/^[A-Za-z0-9_-]{1,100}$/)).min(1).max(1000)}).strict().parse(JSON.parse(boundedFile(configFile,131072).toString()));
  const pem=boundedFile(options.publicKeyFile,8192).toString();if(!pem.startsWith('-----BEGIN PUBLIC KEY-----'))throw new Error('Only a public firmware verification key may be configured');const key=createPublicKey(pem);if(key.asymmetricKeyType!=='ec'||key.asymmetricKeyDetails?.namedCurve!=='prime256v1')throw new Error('Firmware release key must be P-256');
  const manifest=boundedFile(join(options.bundleDirectory,'manifest.bin'),176),image=boundedFile(join(options.bundleDirectory,'image.bin'),0x300000);
  if(manifest.length!==176||manifest.subarray(0,8).toString()!=='MRVOTA01'||manifest.subarray(100,112).some(x=>x!==0)||!verify('sha256',manifest.subarray(0,112),{key,dsaEncoding:'ieee-p1363'},manifest.subarray(112)))throw new Error('Invalid firmware release signature or format');
  this.sequence=manifest.readUInt32BE(8);this.minimumSequence=manifest.readUInt32BE(96);this.layout=field(manifest.subarray(80,96));
  const slotBytes=this.layout==='afe-v2'?0x300000:0x1e0000;
  if(!this.sequence||this.minimumSequence>=this.sequence||field(manifest.subarray(48,80))!=='waveshare-esp32s3-audio'||!['afe-v1','afe-v2','owner-v1'].includes(this.layout)||image.length<1024||image.length>slotBytes||image.length!==manifest.readUInt32BE(12)||image[0]!==0xe9||image.readUInt16LE(12)!==9||!createHash('sha256').update(image).digest().equals(manifest.subarray(16,48)))throw new Error('Firmware image does not match its signed manifest');
  this.devices=new Set(options.deviceIds);this.manifest=manifest;this.image=image;
 }
 offeredTo(deviceId:string){return this.devices.has(deviceId);}
 offer(){return {sequence:this.sequence,minimumSequence:this.minimumSequence,board:'waveshare-esp32s3-audio',layout:this.layout,imageBytes:this.image.length,manifestPath:`/api/device/firmware/${this.sequence}/manifest`,imagePath:`/api/device/firmware/${this.sequence}/image`};}
 bytes(part:'manifest'|'image'){return Buffer.from(part==='manifest'?this.manifest:this.image);}
}
