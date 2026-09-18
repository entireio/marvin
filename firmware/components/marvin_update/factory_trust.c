#include "marvin_update_factory.h"
#include "marvin_update_esp.h"
#include "nvs.h"
#include "mbedtls/pk.h"
#include "mbedtls/platform_util.h"
#include <string.h>
bool marvin_update_factory_trust(char *buffer,size_t capacity,const char *layout,uint32_t slot_bytes,marvin_update_trust_t *trust){
 if(trust)memset(trust,0,sizeof(*trust));
 if(!buffer||capacity<2||capacity>1024||!trust)return false;
 memset(buffer,0,capacity);
 if(!layout||((!strcmp(layout,"afe-v2"))?slot_bytes!=0x300000:((strcmp(layout,"afe-v1")&&strcmp(layout,"owner-v1"))||slot_bytes!=0x1e0000)))return false;
 nvs_handle_t nvs;if(nvs_open_from_partition("factory","identity",NVS_READONLY,&nvs)!=ESP_OK)return false;
 size_t size=capacity;bool ok=nvs_get_blob(nvs,"release_pub",buffer,&size)==ESP_OK;nvs_close(nvs);
 ok=ok&&size>1&&size<=capacity&&buffer[size-1]==0&&!memchr(buffer,0,size-1);
 mbedtls_pk_context key;mbedtls_pk_init(&key);
 if(ok)ok=mbedtls_pk_parse_public_key(&key,(const unsigned char*)buffer,size)==0&&mbedtls_pk_can_do(&key,MBEDTLS_PK_ECDSA)&&mbedtls_pk_get_bitlen(&key)==256;
 if(ok)ok=mbedtls_pk_ec(key)->MBEDTLS_PRIVATE(grp).id==MBEDTLS_ECP_DP_SECP256R1;
 mbedtls_pk_free(&key);uint32_t sequence=0;if(ok)ok=marvin_update_esp_sequence(&sequence);
 if(ok)*trust=(marvin_update_trust_t){buffer,"waveshare-esp32s3-audio",layout,sequence,slot_bytes};
 else mbedtls_platform_zeroize(buffer,capacity);
 return ok;
}
bool marvin_update_factory_bootstrap(uint8_t manifest[MARVIN_UPDATE_MANIFEST_BYTES]){
 if(!manifest)return false;
 memset(manifest,0,MARVIN_UPDATE_MANIFEST_BYTES);nvs_handle_t nvs;
 if(nvs_open_from_partition("factory","identity",NVS_READONLY,&nvs)!=ESP_OK)return false;
 size_t size=MARVIN_UPDATE_MANIFEST_BYTES;bool ok=nvs_get_blob(nvs,"boot_manifest",manifest,&size)==ESP_OK&&size==MARVIN_UPDATE_MANIFEST_BYTES;nvs_close(nvs);
 if(!ok)memset(manifest,0,MARVIN_UPDATE_MANIFEST_BYTES);
 return ok;
}
