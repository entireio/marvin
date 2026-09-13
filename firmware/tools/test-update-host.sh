#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
mbed=work/esp-idf/components/mbedtls/mbedtls
cmake -S "$mbed" -B work/mbedtls-host -DENABLE_TESTING=OFF -DENABLE_PROGRAMS=OFF > work/board/update-host-configure.log
cmake --build work/mbedtls-host -j 4 > work/board/update-host-build.log
node --import tsx scripts/update-vectors.ts
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/components/marvin_update/include -I "$mbed/include" firmware/components/marvin_update/manifest.c tests/firmware/update_test.c work/mbedtls-host/library/libmbedcrypto.a -o work/firmware-update-vectors/update-test
UBSAN_OPTIONS=halt_on_error=1 work/firmware-update-vectors/update-test work/firmware-update-vectors
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/components/marvin_update/include -I "$mbed/include" firmware/components/marvin_update/manifest.c firmware/components/marvin_update/transfer.c tests/firmware/update_transfer_test.c work/mbedtls-host/library/libmbedcrypto.a -o work/firmware-update-vectors/transfer-test
UBSAN_OPTIONS=halt_on_error=1 work/firmware-update-vectors/transfer-test work/firmware-update-vectors
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I tests/firmware/update-platform-fixture -I firmware/components/marvin_update/include -I "$mbed/include" firmware/components/marvin_update/esp_writer.c firmware/components/marvin_update/manifest.c tests/firmware/update_platform_test.c work/mbedtls-host/library/libmbedcrypto.a -o work/firmware-update-vectors/platform-test
UBSAN_OPTIONS=halt_on_error=1 work/firmware-update-vectors/platform-test
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I tests/firmware/update-platform-fixture -I firmware/components/marvin_update/include -I "$mbed/include" firmware/components/marvin_update/manifest.c firmware/components/marvin_update/transfer.c firmware/components/marvin_update/download.c tests/firmware/update_download_test.c work/mbedtls-host/library/libmbedcrypto.a -o work/firmware-update-vectors/download-test
UBSAN_OPTIONS=halt_on_error=1 work/firmware-update-vectors/download-test work/firmware-update-vectors
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I tests/firmware/update-platform-fixture -I firmware/components/marvin_update/include -I "$mbed/include" firmware/components/marvin_update/factory_trust.c tests/firmware/update_factory_test.c work/mbedtls-host/library/libmbedcrypto.a -o work/firmware-update-vectors/factory-test
UBSAN_OPTIONS=halt_on_error=1 work/firmware-update-vectors/factory-test work/firmware-update-vectors
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -DCONFIG_MARVIN_SIGNED_OTA=1 -DCONFIG_MARVIN_LOCAL_AFE=1 -I tests/firmware/update-platform-fixture -I firmware/components/marvin_update/include -I "$mbed/include" tests/firmware/update_runtime_test.c work/mbedtls-host/library/libmbedcrypto.a -o work/firmware-update-vectors/runtime-test
UBSAN_OPTIONS=halt_on_error=1 work/firmware-update-vectors/runtime-test
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I tests/firmware/update-platform-fixture -I firmware/components/marvin_update/include -I "$mbed/include" firmware/components/marvin_update/esp_writer.c firmware/components/marvin_update/manifest.c tests/firmware/update_bootstrap_test.c work/mbedtls-host/library/libmbedcrypto.a -o work/firmware-update-vectors/bootstrap-test
UBSAN_OPTIONS=halt_on_error=1 work/firmware-update-vectors/bootstrap-test work/firmware-update-vectors
