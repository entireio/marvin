idf_component_register(SRCS "marvin.c" "actuators.c" "motion_controller.c" "board_audio.c" "device_identity.c" "enrollment_client.c" "owner_setup.c" "device_link.c" "device_wire.c" "audio_rate.c" "audio_preroll.c" "body_audio.c" "pet_controls.c" "ota_runtime.c" INCLUDE_DIRS "." REQUIRES marvin_update marvin_enrollment marvin_transfer marvin_journal protocomm nvs_flash esp_wifi esp_event esp_netif esp_http_client mbedtls json esp_codec_dev esp_driver_gpio esp_driver_i2c esp_driver_i2s esp_driver_ledc esp_websocket_client)
idf_component_optional_requires(PRIVATE afe)

# Real-time FIR processing must remain optimized in otherwise-debug bench builds.
set_source_files_properties(audio_rate.c PROPERTIES COMPILE_OPTIONS "-O2")
