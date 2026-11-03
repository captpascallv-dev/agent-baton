// Purpose-specific streaming projection for Codex IPC v11. Private string
// values/subtrees are scanned as bytes and never decoded, packed, or retained.
const patterns = [
  ["type"], ["method"], ["sourceClientId"], ["requestId"], ["version"], ["resultType"], ["handledByClientId"], ["error"],
  ["result", "clientId"], ["params", "clientId"], ["params", "status"], ["params", "hostId"], ["params", "conversationId"],
  ["targetClientIds", "*"], ["params", "change", "type"], ["params", "change", "revision"], ["params", "change", "baseRevision"],
  ["params", "change", "patches", "*", "op"], ["params", "change", "patches", "*", "path", "*"],
];
const conversationPatterns = [
  ["threadRuntimeStatus", "type"], ["threadRuntimeStatus", "activeFlags", "*"], ["turnHistory", "kind"],
  ...["status", "turnId"].map(field => ["turnHistory", "history", "entitiesByKey", "*", field]),
  ["turnHistory", "history", "entitiesByKey", "*", "params", "model"],
  ["turnHistory", "history", "entitiesByKey", "*", "params", "collaborationMode", "settings", "model"],
  ...["status", "turnId"].map(field => ["turns", "*", field]), ["turns", "*", "params", "model"],
  ["turns", "*", "params", "collaborationMode", "settings", "model"],
];
const prefix = (path, pattern) => path.length <= pattern.length && path.every((part, i) => pattern[i] === "*" || pattern[i] === part);
export function relevantCodexPath(path) { return Array.isArray(path) && conversationPatterns.some(pattern => prefix(path, pattern)); }

export class CodexMetadataJson {
  constructor() { this.stack = []; this.output = undefined; this.done = false; this.token = null; this.skip = null; this.selectedBytes = 0; this.peakTokenBytes = 0; }
  path() {
    const parent = this.stack.at(-1);
    return parent ? [...parent.path, parent.kind === "array" ? parent.index : parent.key] : [];
  }
  wanted(path) {
    const snapshot = ["params", "change", "conversationState"];
    if (prefix(path.slice(0, Math.min(path.length, 3)), snapshot)) {
      if (path.length <= 3) return true;
      return relevantCodexPath(path.slice(3));
    }
    if (path[0] === "params" && path[1] === "change" && path[2] === "patches" && path[4] === "value") {
      const patch = this.output?.params?.change?.patches?.[path[3]];
      if (!Array.isArray(patch?.path)) throw new Error("patch_path_order");
      return relevantCodexPath([...patch.path, ...path.slice(5)]);
    }
    return patterns.some(pattern => prefix(path, pattern));
  }
  complete(value, retain = true) {
    const parent = this.stack.at(-1);
    if (!parent) { if (retain) this.output = value; this.done = true; return; }
    if (retain) {
      const key = parent.kind === "array" ? parent.index : parent.key;
      Object.defineProperty(parent.target, key, { value, writable: true, enumerable: true, configurable: true });
    } else delete parent.target[parent.kind === "array" ? parent.index : parent.key];
    if (parent.kind === "array") parent.index++;
    parent.mode = "comma";
  }
  close(kind) {
    const frame = this.stack.pop();
    if (!frame || frame.kind !== kind || !["key", "value", "comma"].includes(frame.mode)) throw new Error("json_container");
    let retain = true;
    const p = frame.path;
    if (p[0] === "params" && p[1] === "change" && p[2] === "conversationState") {
      const relative = p.slice(3);
      if ((relative.length === 4 && relative.slice(0, 3).join("/") === "turnHistory/history/entitiesByKey")
        || relative.length === 2 && relative[0] === "turns") retain = frame.target.status === "inProgress";
    }
    if (p.length === 4 && p.slice(0, 3).join("/") === "params/change/patches") retain = relevantCodexPath(frame.target.path);
    this.complete(frame.target, retain);
  }
  write(bytes) {
    let i = 0;
    while (i < bytes.length) {
      const c = bytes[i];
      if (this.skip) {
        const s = this.skip;
        if (s.string) { if (s.escape) s.escape = false; else if (c === 92) s.escape = true; else if (c === 34) s.string = false; }
        else if (c === 34) s.string = true;
        else if (c === 123 || c === 91) s.depth++;
        else if (c === 125 || c === 93) { s.depth--; if (s.depth === 0) { this.skip = null; this.complete(undefined, false); } }
        i++; continue;
      }
      if (this.token) {
        const t = this.token;
        if (t.kind === "string") {
          if (t.capture) {
            if (t.bytes.length >= 512) throw new Error("metadata_string_limit");
            t.bytes.push(c); this.peakTokenBytes = Math.max(this.peakTokenBytes, t.bytes.length);
          }
          if (t.escape) t.escape = false;
          else if (c === 92) t.escape = true;
          else if (c === 34) {
            this.token = null;
            const value = t.capture ? JSON.parse(Buffer.from(t.bytes).toString("utf8")) : undefined;
            if (t.capture) this.selectedBytes += t.bytes.length;
            if (t.key) { const f = this.stack.at(-1); f.key = value; f.mode = "colon"; }
            else this.complete(value, t.capture);
          }
          i++; continue;
        }
        if (c === 44 || c === 93 || c === 125 || c <= 32) {
          this.token = null;
          this.complete(t.capture ? JSON.parse(t.text) : undefined, t.capture); continue;
        }
        if (t.text.length >= 64) throw new Error("metadata_literal_limit");
        t.text += String.fromCharCode(c); i++; continue;
      }
      if (c <= 32) { i++; continue; }
      const parent = this.stack.at(-1);
      if (parent?.mode === "colon") { if (c !== 58) throw new Error("json_colon"); parent.mode = "value"; i++; continue; }
      if (parent?.mode === "comma") {
        if (c === 44) { parent.mode = parent.kind === "object" ? "key" : "value"; i++; continue; }
        if (c === 125 || c === 93) { this.close(c === 125 ? "object" : "array"); i++; continue; }
        throw new Error("json_comma");
      }
      if (parent?.kind === "object" && parent.mode === "key") {
        if (c === 125) { this.close("object"); i++; continue; }
        if (c !== 34) throw new Error("json_key");
        this.token = { kind: "string", key: true, capture: true, bytes: [34], escape: false }; i++; continue;
      }
      if (parent?.kind === "array" && parent.mode === "value" && c === 93) { this.close("array"); i++; continue; }
      if (this.done && !parent) throw new Error("json_trailing");
      const path = this.path(), capture = this.wanted(path);
      if (c === 123 || c === 91) {
        if (!capture) this.skip = { depth: 1, string: false, escape: false };
        else {
          const target = c === 123 ? {} : [];
          // Attach immediately so a patch's already-projected path is visible
          // when its following value token is considered.
          if (parent) {
            const key = parent.kind === "array" ? parent.index : parent.key;
            Object.defineProperty(parent.target, key, { value: target, writable: true, enumerable: true, configurable: true });
          } else this.output = target;
          if (this.stack.length >= 32) throw new Error("metadata_depth_limit");
          this.stack.push({ kind: c === 123 ? "object" : "array", target, path, mode: c === 123 ? "key" : "value", index: 0, key: null });
        }
        i++; continue;
      }
      if (c === 34) { this.token = { kind: "string", key: false, capture, bytes: capture ? [34] : [], escape: false }; i++; continue; }
      if (c === 116 || c === 102 || c === 110 || c === 45 || c >= 48 && c <= 57) {
        this.token = { kind: "literal", capture, text: String.fromCharCode(c) }; i++; continue;
      }
      throw new Error("json_value");
    }
  }
  finish() {
    if (this.token?.kind === "literal") { const t = this.token; this.token = null; this.complete(t.capture ? JSON.parse(t.text) : undefined, t.capture); }
    if (!this.done || this.stack.length || this.skip || this.token) throw new Error("json_incomplete");
    return this.output;
  }
}
