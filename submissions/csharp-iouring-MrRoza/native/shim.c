/* The SQLite amalgamation, unmodified, plus thin wrappers that expose its B-tree cursor API to C#.
 * Compiled as one translation unit so the wrappers inline into SQLite's own (static) functions.
 * Reads use these cursors inside the batch's read transaction; every byte still comes from SQLite. */
#include "sqlite3.c"

int tw_cursor_size(void) { return sqlite3BtreeCursorSize(); }

/* The main database's Btree of a connection. */
void *tw_btree(sqlite3 *db) { return db->aDb[0].pBt; }

/* KeyInfo for an index of nKey key columns plus nExtra trailing ones; bit i of descMask = column i is DESC. */
void *tw_keyinfo(sqlite3 *db, int nKey, int nExtra, unsigned descMask) {
  KeyInfo *k = sqlite3KeyInfoAlloc(db, nKey, nExtra);
  int i;
  if (!k) return 0;
  for (i = 0; i < nKey + nExtra; i++) {
    k->aColl[i] = 0; /* BINARY */
    k->aSortFlags[i] = ((descMask >> i) & 1) ? KEYINFO_ORDER_DESC : 0;
  }
  return k;
}

int tw_open(void *bt, unsigned root, void *keyInfo, void *cur) {
  sqlite3BtreeCursorZero((BtCursor *)cur);
  return sqlite3BtreeCursor((Btree *)bt, (Pgno)root, 0, (KeyInfo *)keyInfo, (BtCursor *)cur);
}

int tw_close(void *cur) { return sqlite3BtreeCloseCursor((BtCursor *)cur); }

/* *empty = 1 if the b-tree has no entries. */
int tw_first(void *cur, int *empty) { return sqlite3BtreeFirst((BtCursor *)cur, empty); }

/* SQLITE_DONE past the last entry. */
int tw_next(void *cur) { return sqlite3BtreeNext((BtCursor *)cur, 0); }

/* Table b-tree: *res == 0 when the cursor landed on exactly this rowid. */
int tw_seek_rowid(void *cur, sqlite3_int64 rowid, int *res) {
  return sqlite3BtreeTableMoveto((BtCursor *)cur, rowid, 0, res);
}

/* Index b-tree whose first column is an integer: position on the first entry >= v (OP_SeekGE).
 * *eof = 1 if there is none. */
int tw_seek_ge_int(void *cur, void *keyInfo, sqlite3_int64 v, int *eof) {
  UnpackedRecord r;
  Mem m;
  int res = 0, rc;
  memset(&r, 0, sizeof r);
  memset(&m, 0, sizeof m);
  m.flags = MEM_Int;
  m.u.i = v;
  r.pKeyInfo = (KeyInfo *)keyInfo;
  r.aMem = &m;
  r.nField = 1;
  r.default_rc = 1; /* SeekGE: an equal prefix sorts after the key */
  rc = sqlite3BtreeIndexMoveto((BtCursor *)cur, &r, &res);
  if (rc != SQLITE_OK) return rc;
  if (res < 0) {
    rc = sqlite3BtreeNext((BtCursor *)cur, 0);
    if (rc == SQLITE_DONE) { *eof = 1; return SQLITE_OK; }
    if (rc != SQLITE_OK) return rc;
  }
  *eof = sqlite3BtreeEof((BtCursor *)cur);
  return SQLITE_OK;
}

sqlite3_int64 tw_rowid(void *cur) { return sqlite3BtreeIntegerKey((BtCursor *)cur); }

/* The record of the current entry: pointer to its local bytes (*local of them) and its *total size.
 * If local < total the rest is on overflow pages: use tw_payload_copy. */
const void *tw_payload(void *cur, unsigned *local, unsigned *total) {
  *total = sqlite3BtreePayloadSize((BtCursor *)cur);
  return sqlite3BtreePayloadFetch((BtCursor *)cur, local);
}

int tw_payload_copy(void *cur, unsigned offset, unsigned amt, void *buf) {
  return sqlite3BtreePayload((BtCursor *)cur, offset, amt, buf);
}
