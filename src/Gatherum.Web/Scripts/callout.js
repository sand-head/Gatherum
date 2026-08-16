import { Node, mergeAttributes } from '@tiptap/core';

// A block container matching the server-side Markdown form "> [!kind] …".
export const Callout = Node.create({
  name: 'callout',
  group: 'block',
  content: 'block+',
  defining: true,

  addAttributes() {
    return { kind: { default: 'info' } };
  },

  parseHTML() {
    return [{ tag: 'div[data-callout]', getAttrs: (el) => ({ kind: el.getAttribute('data-callout') }) }];
  },

  renderHTML({ node, HTMLAttributes }) {
    return ['div', mergeAttributes(HTMLAttributes, {
      'data-callout': node.attrs.kind,
      class: `callout callout-${node.attrs.kind}`,
    }), 0];
  },

  addCommands() {
    return {
      setCallout: (kind) => ({ commands }) =>
        commands.wrapIn(this.name, { kind }),
    };
  },
});
