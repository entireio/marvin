import { Check, HelpCircle, Link2, MessageSquare, Plus, X } from 'lucide-react';
import type { Conversation } from '../../../packages/contracts/src/index';
import type { useSidebar } from './useSidebar';

type Props = {
  navigation: ReturnType<typeof useSidebar>;
  conversations: Conversation[];
  currentId?: string;
  petConversation: string | null;
  onNew: () => void;
  onChoose: (conversation: Conversation) => void;
  onPetLink: (conversation: Conversation) => void;
};

export function SidebarIcon({ floating = false }: { floating?: boolean }) {
  return <span className={`sidebar-mode-icon ${floating ? 'is-floating' : ''}`} aria-hidden="true"/>;
}
export function AppBrand() {
  return <a className="wordmark" href="/app" aria-label="Marvin"><span className="wordmark-logo" aria-hidden="true"/><span>Marvin</span></a>;
}

export function Sidebar({ navigation: nav, conversations, currentId, petConversation, onNew, onChoose, onPetLink }: Props) {
  return <>
    {nav.drawer && <button className="app-sidebar-backdrop" tabIndex={-1} aria-label="Close navigation" onClick={() => nav.close('trigger')}/>}
    <aside id="app-sidebar" ref={nav.panelRef} className="app-sidebar" aria-label="Conversation navigation"
      role={nav.drawer ? 'dialog' : undefined} aria-modal={nav.drawer || undefined} inert={!nav.visible}
      onMouseEnter={nav.enter} onMouseLeave={nav.leave}>
      <header className="app-sidebar-header">
        <AppBrand/>
        <button className="icon-button sidebar-pin" aria-label={nav.drawer ? 'Close navigation' : nav.pinned ? 'Collapse navigation' : 'Pin sidebar'}
          title={nav.drawer ? 'Close navigation' : nav.pinned ? 'Condense sidebar' : 'Pin sidebar'}
          onClick={() => nav.drawer ? nav.close('trigger') : nav.setPreference(!nav.pinned)}>
          {nav.drawer ? <X size={18}/> : <SidebarIcon floating={!nav.pinned}/>}
        </button>
      </header>
      <div className="app-sidebar-scroll">
        <button className="sidebar-row sidebar-new" aria-label="New conversation" onClick={onNew}>
          <span className="sidebar-icon"><Plus size={16}/></span><span>New conversation</span>
        </button>
        <div className="sidebar-section-heading">
          <span>Conversations</span>
          <details className="sidebar-help">
            <summary aria-label="About Pet links" title="About Pet links"><HelpCircle size={14}/></summary>
            <p>Use the link beside a conversation to choose it for your Pet. Without a link, your Pet starts a new chat. Changes apply when it next listens.</p>
          </details>
        </div>
        <nav className="sidebar-conversations" aria-label="Recent conversations">
          {conversations.map(conversation => <div className="history-row" key={conversation.id} data-conversation-id={conversation.id}>
            <button className={`sidebar-row history-conversation ${currentId === conversation.id ? 'active' : ''}`}
              aria-current={currentId === conversation.id ? 'page' : undefined} onClick={() => onChoose(conversation)}>
              <span className="sidebar-icon"><MessageSquare size={16}/></span><span>{conversation.title}</span>
            </button>
            <button className={`sidebar-pet-link ${petConversation === conversation.id ? 'linked' : ''}`}
              aria-label={petConversation === conversation.id ? `Remove Desktop Pet link from ${conversation.title}` : `Link Desktop Pet to ${conversation.title}`}
              aria-pressed={petConversation === conversation.id} title={petConversation === conversation.id ? 'Pet linked · click to remove' : 'Link Desktop Pet to this conversation'} onClick={() => onPetLink(conversation)}>
              {petConversation === conversation.id ? <Check size={14}/> : <Link2 size={14}/>}
            </button>
          </div>)}
        </nav>
      </div>

    </aside>
  </>;
}
