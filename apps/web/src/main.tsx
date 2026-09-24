import './appearance';
import { createRoot } from 'react-dom/client';
import '../../../packages/design-tokens/tokens.css';
import './style.css';
import './entire.css';
import './sidebar.css';
import { App } from './App';
const root=createRoot(document.getElementById('root')!);
if(import.meta.env.DEV&&new URLSearchParams(location.search).get('preview')==='remote'){
 void import('./RemotePreview').then(({RemotePreview})=>root.render(<RemotePreview/>));
}else root.render(<App/>);
if(import.meta.env.DEV&&new URLSearchParams(location.search).has('audit'))void import('./dev-audit').then(m=>m.installAudit());
