idf_component_register(SRCS "manifest.c" "transfer.c" "esp_writer.c" "download.c" "factory_trust.c" INCLUDE_DIRS "include" REQUIRES mbedtls app_update nvs_flash esp_http_client esp_timer log)
