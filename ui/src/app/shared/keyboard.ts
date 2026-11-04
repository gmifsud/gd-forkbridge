/** Keyboard-shortcut guard shared by inspectors: shortcuts never fire while the user types in a field or editor,
 *  or when a modifier key is held (so Ctrl+Z, Ctrl+A etc. keep their editing meaning). No Angular imports. */
export interface KeyLike { key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; target: unknown; }

export function isEditingTarget(target: unknown): boolean {
  const t = target as { tagName?: string; isContentEditable?: boolean } | null;
  if (!t || typeof t !== 'object') return false;
  return t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable === true;
}

export function shortcutAllowed(e: KeyLike): boolean {
  return !isEditingTarget(e.target) && !e.ctrlKey && !e.metaKey && !e.altKey;
}
