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

/* Looks up keys[lo..hi) (ascending, duplicates allowed) in the table b-tree rooted at pgno, visiting each page
 * once for all the keys below it instead of one root-to-leaf walk per key: one request's 20 feed authors.
 * The search on each page is SQLite's own (sqlite3BtreeTableMoveto: keys <= a cell's key are in its child).
 * For key i: len[i] = record bytes copied to buf at off[i]; -1 = no such row; -2 = the record spills onto
 * overflow pages or buf is full (the caller looks that one up with a cursor). Returns an SQLite error code. */
static int twLookup(BtShared *pBt, Pgno pgno, int depth, const sqlite3_int64 *keys, int lo, int hi,
                    unsigned char *buf, int cap, int *used, int *off, int *len) {
  MemPage *pPage;
  int rc, k, idx = 0;
  if (depth >= BTCURSOR_MAX_DEPTH) return SQLITE_CORRUPT_BKPT;
  rc = getAndInitPage(pBt, pgno, &pPage, 0);
  if (rc) return rc;
  if (!pPage->intKey || (depth > 0 && pPage->nCell < 1)) { releasePage(pPage); return SQLITE_CORRUPT_BKPT; }
  for (k = lo; k < hi && rc == SQLITE_OK;) {
    sqlite3_int64 key = keys[k];
    int l = idx, u = pPage->nCell - 1;
    while (l <= u) { /* first cell at or after idx whose key >= key */
      int m = (l + u) >> 1;
      i64 ck;
      u8 *pCell = findCellPastPtr(pPage, m);
      if (pPage->intKeyLeaf) {
        while (0x80 <= *(pCell++))
          if (pCell >= pPage->aDataEnd) { releasePage(pPage); return SQLITE_CORRUPT_BKPT; }
      }
      getVarint(pCell, (u64 *)&ck);
      if (ck < key) l = m + 1; else u = m - 1;
    }
    idx = l;
    if (pPage->leaf) {
      len[k] = -1;
      if (idx < pPage->nCell) {
        CellInfo info;
        pPage->xParseCell(pPage, findCell(pPage, idx), &info);
        if (info.nKey == key) {
          if (info.nLocal < info.nPayload || *used + (int)info.nPayload > cap) len[k] = -2;
          else {
            memcpy(buf + *used, info.pPayload, info.nPayload);
            off[k] = *used; len[k] = (int)info.nPayload; *used += (int)info.nPayload;
          }
        }
      }
      k++;
    } else {
      Pgno child;
      int k2 = k;
      if (idx >= pPage->nCell) { child = get4byte(&pPage->aData[pPage->hdrOffset + 8]); k2 = hi; }
      else {
        i64 ck;
        getVarint(findCellPastPtr(pPage, idx), (u64 *)&ck);
        child = get4byte(findCell(pPage, idx));
        while (k2 < hi && keys[k2] <= ck) k2++;
      }
      rc = twLookup(pBt, child, depth + 1, keys, k, k2, buf, cap, used, off, len);
      k = k2;
    }
  }
  releasePage(pPage);
  return rc;
}

/* Many rowids at once in the table the cursor is open on (inside its read transaction). */
int tw_lookup_many(void *cur, const sqlite3_int64 *keys, int n, void *buf, int cap, int *off, int *len) {
  BtCursor *c = (BtCursor *)cur;
  int used = 0;
  return twLookup(c->pBt, c->pgnoRoot, 0, keys, 0, n, (unsigned char *)buf, cap, &used, off, len);
}
