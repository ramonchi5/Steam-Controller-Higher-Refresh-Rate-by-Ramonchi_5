#include "bonds.h"
#include "config.h"
#include "haptics.h"
#include "radio.h"
#include <Adafruit_LittleFS.h>
#include <InternalFileSystem.h>
#include <string.h>
using namespace Adafruit_LittleFS_Namespace;

Slot g_slot[NSLOT];
unsigned long g_connReplyMs[NSLOT] = { 0 };
volatile bool g_dirty = false;
bool g_pairing = false;

#define BOND_FILE "/bonds.bin"

bool recEmpty(const uint8_t *r)
{
	for (int i = 0; i < 24; i++)
		if (r[i])
			return false;
	return true;
}

void saveBonds()
{
	InternalFS.remove(BOND_FILE);
	File f(InternalFS);
	if (f.open(BOND_FILE, FILE_O_WRITE)) {
		for (int i = 0; i < NSLOT; i++) {
			uint8_t u = g_slot[i].used ? 1 : 0;
			f.write(&u, 1);
			f.write(g_slot[i].rec, 24);
		}
		f.close();
	}
}

void evictDuplicateSerialBonds(int keepSlot)
{
	if (keepSlot < 0 || keepSlot >= NSLOT || !g_slot[keepSlot].used)
		return;
	const uint8_t *ser = g_slot[keepSlot].rec + 8;
	if (ser[0] == 0)
		return;
	for (int s = 0; s < NSLOT; s++) {
		if (s == keepSlot || !g_slot[s].used)
			continue;
		if (memcmp(g_slot[s].rec + 8, ser, 16) == 0) {
			g_slot[s].used = false;
			memset(g_slot[s].rec, 0, 24);
			g_connReplyMs[s] = 0;
			relayClearSlot((uint8_t)s);
			rfGenSessionAddr(s);
			g_dirty = true;
		}
	}
}

void loadBonds()
{
	File f(InternalFS);
	if (f.open(BOND_FILE, FILE_O_READ))
		for (int i = 0; i < NSLOT; i++) {
			uint8_t u = 0;
			if (f.read(&u, 1) == 1) {
				f.read(g_slot[i].rec, 24);
				g_slot[i].used = u && !recEmpty(g_slot[i].rec);
			}
		}
	f.close();

	// If the same physical controller (16-byte serial at rec+8) was re-paired into a higher slot while its
	// older bond remained in a lower slot, evict the stale lower slot so it never starves RF/HID slot 0.
	for (int s = NSLOT - 1; s >= 0; s--) {
		if (!g_slot[s].used || g_slot[s].rec[8] == 0)
			continue;
		for (int j = 0; j < s; j++) {
			if (g_slot[j].used &&
			    memcmp(g_slot[j].rec + 8, g_slot[s].rec + 8, 16) == 0) {
				g_slot[j].used = false;
				memset(g_slot[j].rec, 0, 24);
				g_dirty = true;
			}
		}
	}

	// In single-controller mode, keep a lone bond in slot 0 (MI_02) so both Steam and single-interface
	// raw-HID tools (e.g. Gamepadla Polling 2) always find the active controller on the first interface.
	if (g_singleCtrlMode && !g_slot[0].used && bondedSlotCount() == 1) {
		for (int s = 1; s < NSLOT; s++) {
			if (g_slot[s].used) {
				memcpy(g_slot[0].rec, g_slot[s].rec, 24);
				g_slot[0].used = true;
				memset(g_slot[s].rec, 0, 24);
				g_slot[s].used = false;
				g_dirty = true;
				break;
			}
		}
	}
}

int bondedSlotCount()
{
	int n = 0;
	for (int i = 0; i < NSLOT; i++)
		if (g_slot[i].used)
			n++;
	return n;
}
