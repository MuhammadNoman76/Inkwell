// Generates Inkwell's UI icon resources from Microsoft's Fluent UI System Icons (MIT licence),
// package @fluentui/svg-icons (vector SVGs). No icon font is used, so the icons render on Windows 7 too.
//
//   npm pack @fluentui/svg-icons@1.1.344            (in an empty scratch folder)
//   tar -xzf fluentui-svg-icons-1.1.344.tgz
//   node tools/make-icons.mjs <scratch>/package
//
// Writes src/Inkwell/Themes/Icons.xaml: the app's own UI icons (20 x 20, Geometry resources "Icon.Name").
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const pkg = process.argv[2];
if (!pkg || !fs.existsSync(path.join(pkg, 'icons'))) {
  console.error('usage: node tools/make-icons.mjs <path to the extracted @fluentui/svg-icons package>');
  process.exit(1);
}
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const version = JSON.parse(fs.readFileSync(path.join(pkg, 'package.json'), 'utf8')).version;
const iconsDir = path.join(pkg, 'icons');

// ------------------------------------------------------------------ SVG path data -> WPF path markup

const arity = { m: 2, l: 2, h: 1, v: 1, c: 6, s: 4, q: 4, t: 2, a: 7, z: 0 };

function parsePath(d) {
  const out = [];
  let i = 0;
  const n = d.length;
  const skip = () => { while (i < n && /[\s,]/.test(d[i])) i++; };
  const number = () => {
    skip();
    const m = /^[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?/.exec(d.slice(i));
    if (!m) throw new Error('number expected at ' + i + ' in ' + d.slice(i, i + 20));
    i += m[0].length;
    return parseFloat(m[0]);
  };
  const flag = () => {
    skip();
    const c = d[i];
    if (c !== '0' && c !== '1') throw new Error('arc flag expected at ' + i);
    i++;
    return c === '1' ? 1 : 0;
  };
  let cmd = null;
  while (true) {
    skip();
    if (i >= n) break;
    const c = d[i];
    if (/[a-zA-Z]/.test(c)) {
      cmd = c;
      i++;
    } else if (cmd === null) {
      throw new Error('path must start with a command');
    } else if (cmd === 'M') {
      cmd = 'L'; // extra pairs after a moveto are linetos
    } else if (cmd === 'm') {
      cmd = 'l';
    }
    const lower = cmd.toLowerCase();
    const k = arity[lower];
    if (k === undefined) throw new Error('unknown command ' + cmd);
    if (k === 0) {
      out.push({ cmd: 'Z', args: [] });
      continue;
    }
    const args = [];
    for (let j = 0; j < k; j++) args.push(lower === 'a' && (j === 3 || j === 4) ? flag() : number());
    out.push({ cmd, args });
  }
  return out;
}

const fmt = (v) => {
  const r = Math.round(v * 1000) / 1000;
  return (Object.is(r, -0) ? 0 : r).toString();
};

function toWpf(d, evenOdd) {
  const parts = [evenOdd ? 'F0' : 'F1'];
  for (const seg of parsePath(d)) {
    parts.push(seg.cmd);
    for (const a of seg.args) parts.push(fmt(a));
  }
  return parts.join(' ');
}

function readIcon(file) {
  const svg = fs.readFileSync(file, 'utf8');
  const paths = [...svg.matchAll(/<path\b([^>]*)\/?>/g)];
  if (paths.length === 0) throw new Error('no path in ' + file);
  const ds = [];
  let evenOdd = false;
  for (const p of paths) {
    const attrs = p[1];
    const d = /\sd="([^"]+)"/.exec(attrs);
    if (!d) continue;
    if (/fill-rule="evenodd"/.test(attrs)) evenOdd = true;
    ds.push(d[1]);
  }
  return toWpf(ds.join(' '), evenOdd);
}

const pascal = (snake) => snake.split('_').map((w) => w.charAt(0).toUpperCase() + w.slice(1)).join('');

// ------------------------------------------------------------------ UI icons (20 px)

// Fluent icon names; the resource key is the name in PascalCase (text_header_1 -> Icon.TextHeader1).
const ui = [
  // file and app
  'add', 'arrow_undo', 'arrow_redo', 'save', 'save_copy', 'folder_open', 'open', 'document', 'document_add', 'document_text',
  'document_pdf', 'document_multiple', 'document_copy', 'document_search', 'document_edit', 'document_link', 'arrow_export_ltr',
  'arrow_import', 'arrow_download', 'arrow_sync', 'share', 'print', 'settings', 'info', 'search', 'dismiss', 'dismiss_circle',
  'more_horizontal', 'more_vertical', 'chevron_down', 'chevron_up', 'chevron_left', 'chevron_right', 'checkmark', 'checkmark_circle',
  'warning', 'error_circle', 'question_circle', 'history', 'pin', 'pin_off', 'dark_theme', 'weather_moon', 'globe', 'keyboard',
  'window_new', 'home', 'delete', 'copy', 'cut', 'clipboard_paste', 'edit', 'rename', 'eye', 'eye_off', 'lock_closed', 'lock_open',
  'clock', 'timer', 'calendar', 'calendar_ltr', 'arrow_reset', 'arrow_left', 'arrow_right', 'arrow_up', 'arrow_down',
  'arrow_maximize', 'arrow_minimize', 'arrow_move', 'arrow_reply', 'arrow_clockwise', 'arrow_counterclockwise', 'arrow_swap',
  'arrow_trending', 'arrow_sort_down', 'arrow_sort_up', 'apps', 'desktop', 'flash', 'lightbulb', 'sparkle', 'wand', 'star', 'tag',
  'filter', 'mail', 'person', 'people', 'library', 'book', 'book_open', 'book_question_mark', 'bookmark', 'bookmark_add',
  'local_language', 'translate', 'signature', 'scan_text', 'slash_forward', 'code', 'code_block', 'emoji', 'cursor', 'branch',
  're_order_dots_vertical',
  // view
  'panel_left', 'panel_right', 'panel_left_contract', 'panel_right_contract', 'navigation', 'zoom_in', 'zoom_out', 'zoom_fit',
  'page_fit', 'ruler', 'grid', 'grid_dots', 'full_screen_maximize', 'full_screen_minimize', 'glasses', 'eye_tracking',
  'center_vertical', 'target', 'document_one_page', 'document_landscape', 'document_margins',
  // insert
  'image', 'image_add', 'image_alt_text', 'image_edit', 'image_shadow', 'crop', 'shapes', 'square', 'circle', 'triangle',
  'connector', 'line_horizontal_1', 'textbox', 'text_add_t', 'table', 'table_add', 'table_insert_row', 'table_insert_column',
  'table_delete_row', 'table_delete_column', 'table_cells_merge', 'table_cells_split', 'table_resize_column', 'table_freeze_row',
  'table_settings', 'chart_multiple', 'data_bar_vertical', 'data_pie', 'data_line', 'diagram', 'content_view', 'icons', 'symbols',
  'math_formula', 'number_symbol', 'link', 'link_add', 'link_dismiss', 'document_header', 'document_footer',
  'document_header_footer', 'document_page_break', 'document_split_hint', 'document_page_number', 'document_bullet_list',
  'document_table', 'text_footnote', 'apps_list', 'list_bar', 'text_bullet_list_tree', 'hand_draw', 'inking_tool', 'eraser', 'pen',
  'highlight',
  // review
  'comment', 'comment_add', 'comment_multiple', 'comment_checkmark', 'comment_dismiss', 'chat', 'note', 'notepad',
  'text_proofing_tools', 'text_word_count', 'text_whole_word',
  // text and paragraphs
  'text_t', 'text_bold', 'text_italic', 'text_underline', 'text_strikethrough', 'text_subscript', 'text_superscript', 'text_font',
  'text_font_size', 'text_color', 'color', 'color_background', 'paint_brush', 'paint_bucket', 'text_effects', 'text_edit_style',
  'text_clear_formatting', 'text_case_title', 'text_case_uppercase', 'text_change_case', 'text_header_1', 'text_header_2',
  'text_header_3', 'text_paragraph', 'text_quote', 'text_align_left', 'text_align_center', 'text_align_right', 'text_align_justify',
  'text_align_distributed_vertical', 'text_line_spacing', 'text_indent_increase_ltr', 'text_indent_decrease_ltr',
  'text_indent_increase_rtl', 'text_indent_decrease_rtl', 'text_hanging', 'text_bullet_list_ltr', 'text_bullet_list_rtl',
  'text_bullet_list_square', 'text_number_list_ltr', 'text_number_list_rtl', 'text_paragraph_direction_left',
  'text_paragraph_direction_right', 'text_direction_rotate_90_right', 'text_column_one', 'text_column_two', 'text_column_three',
  'text_wrap', 'text_wrap_off', 'text_sort_ascending', 'text_sort_descending', 'textbox_align_top', 'textbox_align_middle',
  'textbox_align_bottom',
  // pictures, shapes and borders
  'text_position_line', 'text_position_square', 'text_position_tight', 'text_position_through', 'text_position_top_bottom',
  'text_position_behind', 'text_position_front', 'position_to_front', 'position_to_back', 'position_forward', 'position_backward',
  'align_left', 'align_center_horizontal', 'align_right', 'align_top', 'align_center_vertical', 'align_bottom', 'group',
  'group_dismiss', 'flip_horizontal', 'flip_vertical', 'arrow_rotate_clockwise', 'resize', 'color_fill', 'color_line', 'border_all',
  'border_outside', 'border_outside_thick', 'border_inside', 'border_none', 'border_top', 'border_bottom', 'border_left',
  'border_right', 'line_thickness', 'line_style', 'layer', 'square_shadow', 'square_multiple',
];

// Keys named for what they do in Inkwell, drawn with the closest Fluent icon (Fluent has no icon of that name).
const aliases = {
  Accept: 'checkmark_circle',            // accept a tracked change
  Anchor: 'attach',                      // a floating picture's anchor paragraph (Fluent has no anchor)
  ClipboardText: 'clipboard_text_ltr',   // clipboard_text exists only at 32 px
  Focus: 'eye_tracking',                 // focus mode
  Reject: 'dismiss_circle',              // reject a tracked change
  TableOfContents: 'text_bullet_list_tree',
  TextCaption: 'subtitles',              // insert a caption under a picture or table
  TextGrow: 'font_increase',             // grow font
  TextShrink: 'font_decrease',           // shrink font
  TrackChanges: 'document_edit',
  Typewriter: 'center_vertical',         // typewriter scrolling: the current line stays in the middle
};

const uiFilled = ['checkmark_circle', 'circle', 'error_circle', 'info', 'warning', 'pin', 'lock_closed', 'star', 'comment', 'bookmark'];

function uiIcon(name, style) {
  for (const size of [20, 24, 16]) {
    const f = path.join(iconsDir, `${name}_${size}_${style}.svg`);
    if (fs.existsSync(f)) {
      let data = readIcon(f);
      if (size !== 20) data = scaleNote(data, size);
      return data;
    }
  }
  throw new Error('missing UI icon ' + name + ' ' + style);
}

// A 24 / 16 px icon in a 20 px slot: wrap the geometry in a scale (WPF path markup can't, so we scale the numbers).
function scaleNote(data, size) {
  const k = 20 / size;
  const tokens = data.split(' ');
  const out = [tokens[0]];
  let cmd = null;
  let idx = 0;
  for (let t = 1; t < tokens.length; t++) {
    const tok = tokens[t];
    if (/^[a-zA-Z]$/.test(tok)) {
      cmd = tok.toLowerCase();
      idx = 0;
      out.push(tok);
      continue;
    }
    const v = parseFloat(tok);
    // arc: rx ry rotation large sweep x y - only radii and the end point scale
    const scaled = cmd === 'a' ? ([0, 1, 5, 6].includes(idx % 7) ? v * k : v) : v * k;
    out.push(fmt(scaled));
    idx++;
  }
  return out.join(' ');
}

const keys = new Set();
const entry = (key, data) => {
  if (keys.has(key)) throw new Error('duplicate key Icon.' + key);
  keys.add(key);
  return `    <Geometry x:Key="Icon.${key}">${data}</Geometry>\n`;
};

let xaml = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"\n' +
  '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n' +
  '    <!-- Fluent UI System Icons (Copyright (c) 2020 Microsoft Corporation, MIT License), 20x20 coordinate space.\n' +
  `         Generated by tools/make-icons.mjs from @fluentui/svg-icons ${version}; do not edit by hand. -->\n`;
for (const name of [...new Set(ui)].sort()) xaml += entry(pascal(name), uiIcon(name, 'regular'));
xaml += '\n    <!-- Named for what they do in Inkwell; drawn with the closest Fluent icon. -->\n';
for (const key of Object.keys(aliases).sort()) {
  xaml += `    <!-- ${key}: Fluent "${aliases[key]}" -->\n` + entry(key, uiIcon(aliases[key], 'regular'));
}
xaml += '\n';
for (const name of [...new Set(uiFilled)].sort()) xaml += entry(pascal(name) + 'Filled', uiIcon(name, 'filled'));
xaml += '</ResourceDictionary>\n';
fs.mkdirSync(path.join(root, 'src/Inkwell/Themes'), { recursive: true });
fs.writeFileSync(path.join(root, 'src/Inkwell/Themes/Icons.xaml'), xaml, 'utf8');
console.log('Icons.xaml: ' + keys.size + ' icons');
