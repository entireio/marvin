idf_component_register(SRCS "marvin.c" "actuators.c" "motion_controller.c" "connection_choreography.c" "remote_control.c" "gear_vr_controller.c" "ble_remote.c" "ble_voice_frame.c" "board_i2c.c" "board_audio.c" "eyes.c" "eyes_render.c" "battery_level.c" "battery_monitor.c" "device_identity.c" "enrollment_client.c" "owner_setup.c" "device_link.c" "device_wire.c" "audio_rate.c" "audio_preroll.c" "body_audio.c" "pet_controls.c" "ota_runtime.c" INCLUDE_DIRS "." REQUIRES marvin_update marvin_enrollment marvin_transfer marvin_journal protocomm bt nvs_flash esp_wifi esp_event esp_netif esp_http_client esp_adc mbedtls json esp_codec_dev esp_driver_gpio esp_driver_i2c esp_driver_i2s esp_driver_ledc esp_lcd esp_websocket_client)
idf_component_optional_requires(PRIVATE afe)
target_sources(${COMPONENT_LIB} PRIVATE "audio_adpcm.c")

# Real-time FIR processing must remain optimized in otherwise-debug bench builds.
set_source_files_properties(audio_rate.c PROPERTIES COMPILE_OPTIONS "-O2")
