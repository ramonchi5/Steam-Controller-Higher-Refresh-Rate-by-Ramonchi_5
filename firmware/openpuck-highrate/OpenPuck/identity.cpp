#include "identity.h"
#include <Arduino.h>
#include <stdio.h>
#include <string.h>

char g_unit[16];
char g_board[16];
char g_usbSerial[18];

#ifndef OPK_PUCK_FW_TS
// Synced to Valve's current PROTEUS_FW_TS in Steam/bin/hardwareupdater/hardwareupdater.cfg
// so Steam does not falsely nag to flash STM32 Proteus firmware onto the nRF52840 OpenPuck.
#define OPK_PUCK_FW_TS 0x6AC6869Bu
#endif

#define OPK_U32_LE(v) \
	(uint8_t)((v) & 0xFFu), \
	(uint8_t)(((v) >> 8) & 0xFFu), \
	(uint8_t)(((v) >> 16) & 0xFFu), \
	(uint8_t)(((v) >> 24) & 0xFFu)

// clang-format off
const uint8_t ATTR83_PUCK[25] = { // 0x83 attributes
	// 1 byte tag, 4 byte little-endian value.
	0x01,  0x04, 0x13, 0x00, 0x00, 		// product_id = 0x1304
	0x02,  0x00, 0x00, 0x00, 0x00, 		// capabilities = 0 (bitfield)
	0x0A,  0xF2, 0xF9, 0xD2, 0x68,      // boot_build_timestamp = 0x68D2F9F2
	0x04,  OPK_U32_LE(OPK_PUCK_FW_TS),  // build_timestamp = OPK_PUCK_FW_TS (synced with PROTEUS_FW_TS)
	0x09,  0x47, 0x00, 0x00, 0x00       // hw_id = 0x47
};
const uint8_t ATTR83_MACHINE[25] = { // 0x83 attributes
	// 1 byte tag, 4 byte little-endian value.
	0x01,  0x04, 0x13, 0x00, 0x00, 		// product_id = 0x1304. (same as the Puck, interestingly!)
	0x02,  0x00, 0x00, 0x00, 0x00, 		// capabilities = 0 (bitfield)
	0x0A,  0x01, 0x97, 0xCC, 0x68,      // boot_build_timestamp = 0x68CC9701
	0x04,  OPK_U32_LE(OPK_PUCK_FW_TS),  // build_timestamp = OPK_PUCK_FW_TS (synced with PROTEUS_FW_TS)
	0x09,  0x44, 0x00, 0x00, 0x00       // hw_id = 0x44
};
// clang-format on

void genSerial()
{
	uint32_t id = NRF_FICR->DEVICEID[0] ^ NRF_FICR->DEVICEID[1];
	snprintf(g_unit, sizeof g_unit, "FXB99602%05lX",
		 (unsigned long)(id & 0xFFFFF));
	snprintf(g_board, sizeof g_board, "MXB99602%05lX",
		 (unsigned long)(id & 0xFFFFF));
}
