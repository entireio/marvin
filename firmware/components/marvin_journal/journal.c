#include "marvin_journal.h"
#include <string.h>
static void erase(void *data,size_t size){volatile uint8_t *p=data;while(size--)*p++=0;}
static bool text(const char *s,size_t max){return s&&s[0]&&memchr(s,0,max)!=NULL;}
static bool valid(const marvin_journal_record_t *r){
 if(r->version!=MARVIN_JOURNAL_VERSION||r->network_size>MARVIN_NETWORK_BYTES||r->pending_network_size>MARVIN_NETWORK_BYTES)return false;
 if(r->submitted&&!r->pending)return false;
 if(r->linked&&(!r->epoch||!text(r->owner,sizeof(r->owner))||!text(r->credential,sizeof(r->credential))||r->credential_expires_ms<=0||!r->network_size))return false;
 if(r->pending&&(!r->pending_epoch||!text(r->pending_owner,sizeof(r->pending_owner))||!text(r->ticket,sizeof(r->ticket))||!r->pending_network_size))return false;
 if(r->pending&&(r->network_change!=r->linked))return false;
 if(r->pending&&r->network_change&&(r->epoch!=r->pending_epoch||strcmp(r->owner,r->pending_owner)))return false;
 return true;
}
static void clear_pending(marvin_journal_record_t *r){r->pending=false;r->network_change=false;r->submitted=false;r->pending_epoch=0;r->pending_network_size=0;erase(r->pending_owner,sizeof(r->pending_owner));erase(r->ticket,sizeof(r->ticket));erase(r->pending_network,sizeof(r->pending_network));}
static bool publish(marvin_journal_t *j){bool ok=valid(&j->scratch)&&j->save(j->context,&j->scratch);if(ok)memcpy(&j->current,&j->scratch,sizeof(j->current));erase(&j->scratch,sizeof(j->scratch));return ok;}
bool marvin_journal_open(marvin_journal_t *j,const marvin_journal_record_t *saved,marvin_journal_save_t save,void *context){
 if(!j||!save)return false;
 erase(j,sizeof(*j));j->save=save;j->context=context;
 if(saved){if(!valid(saved)){marvin_journal_close(j);return false;}memcpy(&j->current,saved,sizeof(*saved));}else j->current.version=MARVIN_JOURNAL_VERSION;
 return true;
}
bool marvin_journal_stage(marvin_journal_t *j,const marvin_ticket_claims_t *claims,const char *ticket,bool change,const void *network,size_t size){
 if(!j||!j->save||!claims||!text(claims->owner,sizeof(claims->owner))||!claims->epoch||!text(ticket,sizeof(j->current.ticket))||!network||!size||size>MARVIN_NETWORK_BYTES||j->current.pending)return false;
 if(change!=j->current.linked||(change&&(strcmp(claims->owner,j->current.owner)||claims->epoch!=j->current.epoch)))return false;
 memcpy(&j->scratch,&j->current,sizeof(j->scratch));marvin_journal_record_t *r=&j->scratch;
 r->pending=true;r->network_change=change;r->pending_epoch=claims->epoch;r->pending_network_size=(uint16_t)size;
 memcpy(r->pending_owner,claims->owner,strlen(claims->owner)+1);memcpy(r->ticket,ticket,strlen(ticket)+1);memcpy(r->pending_network,network,size);
 return publish(j);
}
bool marvin_journal_submitted(marvin_journal_t *j){if(!j||!j->save||!j->current.pending)return false;if(j->current.submitted)return true;memcpy(&j->scratch,&j->current,sizeof(j->scratch));j->scratch.submitted=true;return publish(j);}
bool marvin_journal_complete(marvin_journal_t *j,uint32_t epoch,const char *credential,int64_t expires){
 if(!j||!j->save||!j->current.pending||!j->current.submitted||epoch!=j->current.pending_epoch)return false;
 if(!j->current.network_change&&(!text(credential,sizeof(j->current.credential))||expires<=0))return false;
 if(j->current.network_change&&credential)return false;
 memcpy(&j->scratch,&j->current,sizeof(j->scratch));marvin_journal_record_t *r=&j->scratch;
 if(!r->network_change){memcpy(r->credential,credential,strlen(credential)+1);r->credential_expires_ms=expires;memcpy(r->owner,r->pending_owner,sizeof(r->owner));r->epoch=epoch;r->linked=true;}
 erase(r->network,sizeof(r->network));memcpy(r->network,r->pending_network,r->pending_network_size);r->network_size=r->pending_network_size;clear_pending(r);
 return publish(j);
}
bool marvin_journal_cancel(marvin_journal_t *j){if(!j||!j->save||!j->current.pending)return false;memcpy(&j->scratch,&j->current,sizeof(j->scratch));clear_pending(&j->scratch);return publish(j);}
void marvin_journal_close(marvin_journal_t *j){if(j)erase(j,sizeof(*j));}
