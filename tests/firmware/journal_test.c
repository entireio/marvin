#include "marvin_journal.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
static marvin_journal_record_t disk;
static marvin_journal_t journal;
static bool fail_write,ambiguous_write;
static bool save(void *unused,const marvin_journal_record_t *r){(void)unused;if(fail_write)return false;memcpy(&disk,r,sizeof(disk));return !ambiguous_write;}
static void reboot(void){marvin_journal_close(&journal);assert(marvin_journal_open(&journal,&disk,save,NULL));}
int main(void){
 marvin_ticket_claims_t claims={.owner="owner",.id="enrollment",.epoch=7,.expires_at=2000000000};
 const char network[]="synthetic-network",replacement[]="new-network";
 assert(marvin_journal_open(&journal,NULL,save,NULL));
 fail_write=true;assert(!marvin_journal_stage(&journal,&claims,"signed-ticket",false,network,sizeof(network)));assert(!journal.current.pending);
 fail_write=false;assert(marvin_journal_stage(&journal,&claims,"signed-ticket",false,network,sizeof(network)));reboot();assert(journal.current.pending&&!journal.current.linked);
 assert(!journal.current.submitted);fail_write=true;assert(!marvin_journal_submitted(&journal));assert(!journal.current.submitted);fail_write=false;assert(marvin_journal_submitted(&journal));reboot();assert(journal.current.submitted);
 assert(!marvin_journal_stage(&journal,&claims,"other-ticket",false,network,sizeof(network)));
 assert(!marvin_journal_complete(&journal,8,"credential",2000000000000));
 fail_write=true;assert(!marvin_journal_complete(&journal,7,"credential",2000000000000));reboot();assert(journal.current.pending);assert(!strcmp(journal.current.ticket,"signed-ticket"));
 fail_write=false;assert(marvin_journal_complete(&journal,7,"credential",2000000000000));reboot();assert(journal.current.linked&&!journal.current.pending);assert(!strcmp(journal.current.credential,"credential"));
 assert(!marvin_journal_stage(&journal,&claims,"ticket",false,replacement,sizeof(replacement)));
 claims.epoch=8;assert(!marvin_journal_stage(&journal,&claims,"ticket",true,replacement,sizeof(replacement)));claims.epoch=7;
 strcpy(claims.owner,"other");assert(!marvin_journal_stage(&journal,&claims,"ticket",true,replacement,sizeof(replacement)));strcpy(claims.owner,"owner");
 assert(marvin_journal_stage(&journal,&claims,"network-ticket",true,replacement,sizeof(replacement)));reboot();assert(!strcmp((char*)journal.current.network,network));
 fail_write=true;assert(!marvin_journal_cancel(&journal));reboot();assert(journal.current.pending);
 fail_write=false;assert(marvin_journal_cancel(&journal));reboot();assert(!journal.current.pending&&!strcmp((char*)journal.current.network,network));
 assert(marvin_journal_stage(&journal,&claims,"network-ticket-2",true,replacement,sizeof(replacement)));assert(marvin_journal_submitted(&journal));assert(!marvin_journal_complete(&journal,7,"replacement-credential",2000000000000));
 assert(marvin_journal_complete(&journal,7,NULL,0));reboot();assert(!strcmp((char*)journal.current.network,replacement));assert(!strcmp(journal.current.credential,"credential"));assert(journal.current.epoch==7);
 for(size_t i=0;i<sizeof(journal.current.ticket);i++)assert(!journal.current.ticket[i]);
 assert(marvin_journal_stage(&journal,&claims,"ambiguous-ticket",true,network,sizeof(network)));assert(marvin_journal_submitted(&journal));ambiguous_write=true;assert(!marvin_journal_complete(&journal,7,NULL,0));ambiguous_write=false;reboot();assert(!journal.current.pending&&!strcmp((char*)journal.current.network,network));
 disk.version++;assert(!marvin_journal_open(&journal,&disk,save,NULL));assert(!journal.save);
 puts("Enrollment journal: staged claim, replay after lost receipt, network rollback, owner/epoch checks and storage failures passed.");
}
