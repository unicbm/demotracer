/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { CustomCssProfile } from "./appearance";

export const CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY = "demotracer.custom-css-starter-profiles.v4";

const LIGHT_ROOTS = `:root,
:root[data-color-mode="light"],
:root[data-theme="light"]:not([data-color-mode]),
:root[data-theme="system"]:not([data-color-mode])`;

const DARK_ROOTS = `:root[data-color-mode="dark"],
:root[data-theme="dark"]:not([data-color-mode])`;

function preset(
  id: string,
  name: string,
  lightVariables: string,
  darkVariables: string,
): CustomCssProfile {
  return {
    id,
    name,
    css: `/* DemoTracer · ${name} */
${LIGHT_ROOTS} {
  color-scheme: light;
${lightVariables}
}

${DARK_ROOTS} {
  color-scheme: dark;
${darkVariables}
}

@media (prefers-color-scheme: dark) {
  :root[data-theme="system"]:not([data-color-mode]) {
    color-scheme: dark;
${darkVariables}
  }
}

html, body, #root { background: var(--app-bg) !important; }

.app-chrome, .application-toolbar, .app-sidebar, .product-lockup {
  background-color: var(--preset-chrome);
}
.app-sidebar { background-image: var(--preset-sidebar-art); }
.primary-button, .sidebar-import-action {
  color: var(--on-accent);
  background: var(--preset-primary);
  border-color: var(--preset-primary-border);
}
.sidebar-nav-item.is-active {
  color: var(--preset-active-ink);
  background: var(--selected-row);
}
::selection { color: var(--on-accent); background: var(--accent); }
`,
  };
}

export const STARTER_CUSTOM_CSS_PROFILES: readonly CustomCssProfile[] = [
  preset(
    "starter-hanbaiyu",
    "汉白玉",
    `  --app-bg: #e9e4da;
  --workspace: #f2eee5;
  --surface-raised: #fffdf7;
  --surface-subtle: #f7f3ea;
  --surface-muted: #eee8dc;
  --disabled-bg: #e7e0d3;
  --divider: #d8d0c2;
  --divider-strong: #bdb3a3;
  --text-primary: #292c29;
  --text-secondary: #5f625b;
  --text-tertiary: #888980;
  --text-disabled: #aaa99f;
  --trace: #24765f;
  --trace-hover: #1d6853;
  --trace-pressed: #175645;
  --trace-soft: #dcebe4;
  --accent: #24765f;
  --accent-hover: #1d6853;
  --accent-pressed: #175645;
  --accent-soft: #dcebe4;
  --on-accent: #fffdf7;
  --focus-ring: #2d8870;
  --selected-row: #d8e9e1;
  --hover-row: #ebe9df;
  --info: #2f7180;
  --info-soft: #dcebed;
  --team-a: #a96b2d;
  --team-b: #2f7180;
  --side-t: #a96b2d;
  --side-ct: #2f7180;
  --success: #39755a;
  --success-soft: #dfede4;
  --warning: #9a6325;
  --warning-soft: #f5e8cf;
  --danger: #a94339;
  --danger-hover: #91372f;
  --danger-soft: #f3dfda;
  --on-danger: #fffaf3;
  --surface-shadow: 0 1px 1px rgba(73, 65, 51, .06), 0 7px 20px rgba(73, 65, 51, .07);
  --shadow-sheet: 0 16px 42px rgba(59, 53, 43, .16);
  --shadow-dialog: 0 24px 64px rgba(59, 53, 43, .20);
  --preset-chrome: #f8f4eb;
  --preset-sidebar-art: radial-gradient(circle at 18% 12%, rgba(105, 139, 124, .08), transparent 26%), linear-gradient(155deg, rgba(255, 255, 255, .5), transparent 46%);
  --preset-primary: linear-gradient(180deg, #2f856d, #226d58);
  --preset-primary-border: #1b5d4a;
  --preset-active-ink: #175645;`,
    `  --app-bg: #111714;
  --workspace: #171f1b;
  --surface-raised: #202923;
  --surface-subtle: #27322c;
  --surface-muted: #303c35;
  --disabled-bg: #344039;
  --divider: #3a4940;
  --divider-strong: #52675b;
  --text-primary: #f2eee4;
  --text-secondary: #c6c7bc;
  --text-tertiary: #90988e;
  --text-disabled: #626b63;
  --trace: #72b99b;
  --trace-hover: #88c9ad;
  --trace-pressed: #5aa184;
  --trace-soft: #263f35;
  --accent: #72b99b;
  --accent-hover: #88c9ad;
  --accent-pressed: #5aa184;
  --accent-soft: #263f35;
  --on-accent: #10231b;
  --focus-ring: #8bcbb0;
  --selected-row: #29443a;
  --hover-row: #222f29;
  --info: #72b4c0;
  --info-soft: #233b40;
  --team-a: #d3a066;
  --team-b: #72b4c0;
  --side-t: #d3a066;
  --side-ct: #72b4c0;
  --success: #7bc19b;
  --success-soft: #243d31;
  --warning: #d2a461;
  --warning-soft: #42351f;
  --danger: #df8075;
  --danger-hover: #ec968c;
  --danger-soft: #442826;
  --on-danger: #2a0d0a;
  --surface-shadow: 0 1px 0 rgba(255, 255, 255, .025), 0 10px 30px rgba(0, 0, 0, .24);
  --shadow-sheet: 0 20px 54px rgba(0, 0, 0, .42);
  --shadow-dialog: 0 30px 82px rgba(0, 0, 0, .52);
  --preset-chrome: #151c18;
  --preset-sidebar-art: radial-gradient(circle at 18% 12%, rgba(114, 185, 155, .09), transparent 28%), linear-gradient(155deg, rgba(255, 255, 255, .025), transparent 46%);
  --preset-primary: linear-gradient(180deg, #78c0a2, #5ba084);
  --preset-primary-border: #83c6aa;
  --preset-active-ink: #a7dcc5;`,
  ),
  preset(
    "starter-chinese-new-year",
    "中国新年",
    `  --app-bg: #eee7dc;
  --workspace: #f6f0e7;
  --surface-raised: #fffaf1;
  --surface-subtle: #faf3e8;
  --surface-muted: #f1e6d9;
  --disabled-bg: #e9ded1;
  --divider: #dfd0c1;
  --divider-strong: #c6ab93;
  --text-primary: #302722;
  --text-secondary: #65574e;
  --text-tertiary: #8f7c70;
  --text-disabled: #ad9c91;
  --trace: #b13b32;
  --trace-hover: #9b302a;
  --trace-pressed: #812620;
  --trace-soft: #f2ddd7;
  --accent: #b13b32;
  --accent-hover: #9b302a;
  --accent-pressed: #812620;
  --accent-soft: #f2ddd7;
  --on-accent: #fffaf1;
  --focus-ring: #c34a3f;
  --selected-row: #f1d7d0;
  --hover-row: #f5ece1;
  --info: #3f7180;
  --info-soft: #dcebed;
  --team-a: #b13b32;
  --team-b: #3f7180;
  --side-t: #b13b32;
  --side-ct: #3f7180;
  --success: #4f7659;
  --success-soft: #dfebe1;
  --warning: #9a6829;
  --warning-soft: #f3e5ca;
  --danger: #a93631;
  --danger-hover: #922e2a;
  --danger-soft: #f1d9d5;
  --on-danger: #fffaf1;
  --surface-shadow: 0 1px 1px rgba(80, 56, 39, .05), 0 7px 20px rgba(80, 56, 39, .08);
  --shadow-sheet: 0 16px 42px rgba(68, 43, 28, .16);
  --shadow-dialog: 0 24px 64px rgba(68, 43, 28, .21);
  --preset-chrome: #f8eee2;
  --preset-sidebar-art: radial-gradient(circle at 22% 8%, rgba(177, 59, 50, .075), transparent 26%), linear-gradient(155deg, rgba(255, 255, 255, .42), transparent 48%);
  --preset-primary: linear-gradient(180deg, #bd463b, #a5332c);
  --preset-primary-border: #922b26;
  --preset-active-ink: #872a25;`,
    `  --app-bg: #171311;
  --workspace: #201917;
  --surface-raised: #29211d;
  --surface-subtle: #312722;
  --surface-muted: #3a2e28;
  --disabled-bg: #3f332e;
  --divider: #493930;
  --divider-strong: #6d5143;
  --text-primary: #f4e9d8;
  --text-secondary: #d0bcaa;
  --text-tertiary: #9a8272;
  --text-disabled: #6d5a50;
  --trace: #d86155;
  --trace-hover: #e47769;
  --trace-pressed: #b94b42;
  --trace-soft: #4a2925;
  --accent: #d86155;
  --accent-hover: #e47769;
  --accent-pressed: #b94b42;
  --accent-soft: #4a2925;
  --on-accent: #2b0d0a;
  --focus-ring: #e37a6c;
  --selected-row: #4b2b27;
  --hover-row: #342521;
  --info: #72aebb;
  --info-soft: #263a3d;
  --team-a: #dc6a5d;
  --team-b: #72aebb;
  --side-t: #dc6a5d;
  --side-ct: #72aebb;
  --success: #7eb28a;
  --success-soft: #26382b;
  --warning: #d0a052;
  --warning-soft: #40331e;
  --danger: #e2776c;
  --danger-hover: #ec8e84;
  --danger-soft: #4a2927;
  --on-danger: #2a0b08;
  --surface-shadow: 0 1px 0 rgba(255, 241, 218, .025), 0 10px 30px rgba(0, 0, 0, .26);
  --shadow-sheet: 0 20px 54px rgba(0, 0, 0, .44);
  --shadow-dialog: 0 30px 82px rgba(0, 0, 0, .54);
  --preset-chrome: #1c1614;
  --preset-sidebar-art: radial-gradient(circle at 22% 8%, rgba(216, 97, 85, .09), transparent 28%), linear-gradient(155deg, rgba(208, 160, 82, .035), transparent 50%);
  --preset-primary: linear-gradient(180deg, #df6d60, #be4d44);
  --preset-primary-border: #e17a6e;
  --preset-active-ink: #f0a198;`,
  ),
  preset(
    "starter-black-gold",
    "黑金",
    `  --app-bg: #e8e6e0;
  --workspace: #f1efe9;
  --surface-raised: #fbf9f3;
  --surface-subtle: #f2efe7;
  --surface-muted: #e7e3d9;
  --disabled-bg: #dedbd3;
  --divider: #d1ccc0;
  --divider-strong: #aaa28f;
  --text-primary: #211f1a;
  --text-secondary: #5b574d;
  --text-tertiary: #858073;
  --text-disabled: #aaa69d;
  --trace: #8e6c1f;
  --trace-hover: #765815;
  --trace-pressed: #60470f;
  --trace-soft: #eee4c7;
  --accent: #8e6c1f;
  --accent-hover: #765815;
  --accent-pressed: #60470f;
  --accent-soft: #eee4c7;
  --on-accent: #fffaf0;
  --focus-ring: #a37d25;
  --selected-row: #e9dfbf;
  --hover-row: #ece9e1;
  --info: #63747e;
  --info-soft: #e1e8ea;
  --team-a: #9a7420;
  --team-b: #63747e;
  --side-t: #9a7420;
  --side-ct: #63747e;
  --success: #66764d;
  --success-soft: #e5eadb;
  --warning: #987020;
  --warning-soft: #f2e7ca;
  --danger: #9e4f48;
  --danger-hover: #87423c;
  --danger-soft: #efded9;
  --on-danger: #fffaf0;
  --surface-shadow: 0 1px 1px rgba(45, 41, 31, .05), 0 8px 24px rgba(45, 41, 31, .09);
  --shadow-sheet: 0 18px 48px rgba(38, 34, 26, .18);
  --shadow-dialog: 0 28px 72px rgba(38, 34, 26, .24);
  --preset-chrome: #f5f2ea;
  --preset-sidebar-art: linear-gradient(155deg, rgba(142, 108, 31, .08), transparent 40%);
  --preset-primary: linear-gradient(180deg, #a98532, #806019);
  --preset-primary-border: #755714;
  --preset-active-ink: #684d10;`,
    `  --app-bg: #080909;
  --workspace: #0e0f0f;
  --surface-raised: #151616;
  --surface-subtle: #1c1d1d;
  --surface-muted: #242525;
  --disabled-bg: #292929;
  --divider: #34332f;
  --divider-strong: #555044;
  --text-primary: #f1ead8;
  --text-secondary: #c7bea8;
  --text-tertiary: #8f8879;
  --text-disabled: #5f5b52;
  --trace: #d7ae4d;
  --trace-hover: #ecc765;
  --trace-pressed: #b89139;
  --trace-soft: #3b321d;
  --accent: #d7ae4d;
  --accent-hover: #ecc765;
  --accent-pressed: #b89139;
  --accent-soft: #3b321d;
  --on-accent: #17130b;
  --focus-ring: #e3bd5d;
  --selected-row: #332c1b;
  --hover-row: #24231f;
  --info: #cdbb87;
  --info-soft: #2f2b21;
  --team-a: #d7ae4d;
  --team-b: #8f9eaa;
  --side-t: #d7ae4d;
  --side-ct: #8f9eaa;
  --success: #b4bd79;
  --success-soft: #293022;
  --warning: #e4bd5c;
  --warning-soft: #342b1a;
  --danger: #d97970;
  --danger-hover: #e89187;
  --danger-soft: #36201f;
  --on-danger: #190909;
  --surface-shadow: 0 1px 0 rgba(255, 225, 150, .03), 0 10px 30px rgba(0, 0, 0, .36);
  --shadow-sheet: 0 20px 54px rgba(0, 0, 0, .56);
  --shadow-dialog: 0 30px 82px rgba(0, 0, 0, .66);
  --preset-chrome: #0b0c0c;
  --preset-sidebar-art: linear-gradient(155deg, rgba(215, 174, 77, .08), transparent 38%);
  --preset-primary: linear-gradient(180deg, #e5c466, #bd9237);
  --preset-primary-border: #e3bd5d;
  --preset-active-ink: #f0cf78;`,
  ),
  preset(
    "starter-ultraviolet",
    "紫外",
    `  --app-bg: #ebe8f2;
  --workspace: #f3f0f8;
  --surface-raised: #fdfaff;
  --surface-subtle: #f3edf9;
  --surface-muted: #e8e0f1;
  --disabled-bg: #dfd9e6;
  --divider: #d2c7df;
  --divider-strong: #ad99c5;
  --text-primary: #2d2438;
  --text-secondary: #62566e;
  --text-tertiary: #8c7f99;
  --text-disabled: #ada4b6;
  --trace: #7548b5;
  --trace-hover: #6339a2;
  --trace-pressed: #522c8a;
  --trace-soft: #e7daf5;
  --accent: #7548b5;
  --accent-hover: #6339a2;
  --accent-pressed: #522c8a;
  --accent-soft: #e7daf5;
  --on-accent: #fffaff;
  --focus-ring: #8a59c8;
  --selected-row: #e4d6f3;
  --hover-row: #eee8f3;
  --info: #267e9b;
  --info-soft: #dbeef3;
  --team-a: #b34b91;
  --team-b: #267e9b;
  --side-t: #b34b91;
  --side-ct: #267e9b;
  --success: #3f866b;
  --success-soft: #dceee7;
  --warning: #9c6d25;
  --warning-soft: #f3e7cf;
  --danger: #b34162;
  --danger-hover: #9b3452;
  --danger-soft: #f1dce4;
  --on-danger: #fffaff;
  --surface-shadow: 0 2px 3px rgba(54, 35, 76, .04), 0 10px 28px rgba(76, 50, 103, .09);
  --shadow-sheet: 0 18px 48px rgba(54, 35, 76, .18);
  --shadow-dialog: 0 28px 72px rgba(54, 35, 76, .24);
  --preset-chrome: #f6f1fa;
  --preset-sidebar-art: radial-gradient(circle at 15% 12%, rgba(38, 126, 155, .09), transparent 26%), radial-gradient(circle at 90% 72%, rgba(179, 75, 145, .08), transparent 34%);
  --preset-primary: linear-gradient(135deg, #8a59c8, #6754bb);
  --preset-primary-border: #7044aa;
  --preset-active-ink: #59308d;`,
    `  --app-bg: #090615;
  --workspace: #100a22;
  --surface-raised: #19102f;
  --surface-subtle: #221641;
  --surface-muted: #2b1c50;
  --disabled-bg: #302344;
  --divider: #3d2967;
  --divider-strong: #60418e;
  --text-primary: #f6f0ff;
  --text-secondary: #d3c4ea;
  --text-tertiary: #9b8ab9;
  --text-disabled: #655979;
  --trace: #a874ff;
  --trace-hover: #bd93ff;
  --trace-pressed: #8755dc;
  --trace-soft: #382663;
  --accent: #a874ff;
  --accent-hover: #bd93ff;
  --accent-pressed: #8755dc;
  --accent-soft: #382663;
  --on-accent: #160a2b;
  --focus-ring: #c49dff;
  --selected-row: #382465;
  --hover-row: #281946;
  --info: #54d9ff;
  --info-soft: #16364b;
  --team-a: #ff72ca;
  --team-b: #54d9ff;
  --side-t: #ff72ca;
  --side-ct: #54d9ff;
  --success: #65efc0;
  --success-soft: #153e35;
  --warning: #ffd06a;
  --warning-soft: #44351c;
  --danger: #ff668d;
  --danger-hover: #ff83a3;
  --danger-soft: #491d38;
  --on-danger: #210610;
  --surface-shadow: 0 8px 28px rgba(23, 5, 56, .38);
  --shadow-sheet: 0 20px 58px rgba(12, 1, 35, .58);
  --shadow-dialog: 0 30px 84px rgba(8, 0, 28, .7);
  --preset-chrome: #0d081c;
  --preset-sidebar-art: radial-gradient(circle at 15% 12%, rgba(84, 217, 255, .10), transparent 26%), radial-gradient(circle at 90% 72%, rgba(255, 114, 202, .10), transparent 34%);
  --preset-primary: linear-gradient(135deg, #c18fff, #7d6cff);
  --preset-primary-border: #b98aff;
  --preset-active-ink: #e3d2ff;`,
  ),
  preset(
    "starter-monet",
    "莫奈",
    `  --app-bg: #e8ebee;
  --workspace: #eef0ec;
  --surface-raised: #faf8f2;
  --surface-subtle: #f1f0eb;
  --surface-muted: #e5e7e2;
  --disabled-bg: #dfe1de;
  --divider: #d4d5d0;
  --divider-strong: #b9bdba;
  --text-primary: #343b43;
  --text-secondary: #616a70;
  --text-tertiary: #8b9396;
  --text-disabled: #afb3b2;
  --trace: #6f7fae;
  --trace-hover: #5f709f;
  --trace-pressed: #53638d;
  --trace-soft: #e0e3ef;
  --accent: #6f7fae;
  --accent-hover: #5f709f;
  --accent-pressed: #53638d;
  --accent-soft: #e0e3ef;
  --on-accent: #fbfaf5;
  --focus-ring: #7b89b6;
  --selected-row: #dfe4f1;
  --hover-row: #e8e9e5;
  --info: #678e98;
  --info-soft: #dfeaec;
  --team-a: #b28483;
  --team-b: #718da1;
  --side-t: #b28483;
  --side-ct: #718da1;
  --success: #6e8c73;
  --success-soft: #e0e9df;
  --warning: #a67c43;
  --warning-soft: #f1e7d4;
  --danger: #a65f6c;
  --danger-hover: #914e5b;
  --danger-soft: #f0dfe2;
  --on-danger: #fff9f4;
  --surface-shadow: 0 2px 3px rgba(64, 74, 79, .04), 0 10px 28px rgba(91, 105, 112, .08);
  --shadow-sheet: 0 18px 48px rgba(79, 88, 99, .18);
  --shadow-dialog: 0 28px 72px rgba(79, 88, 99, .23);
  --preset-chrome: #f5f3ed;
  --preset-sidebar-art: radial-gradient(circle at 8% 8%, rgba(153, 132, 180, .14), transparent 28%), radial-gradient(circle at 92% 82%, rgba(117, 157, 145, .13), transparent 34%);
  --preset-primary: linear-gradient(135deg, #7c8fbd, #6979a6);
  --preset-primary-border: #65749d;
  --preset-active-ink: #526389;`,
    `  --app-bg: #15191e;
  --workspace: #1b2026;
  --surface-raised: #242a31;
  --surface-subtle: #2a3139;
  --surface-muted: #333b44;
  --disabled-bg: #384049;
  --divider: #414b55;
  --divider-strong: #5c6873;
  --text-primary: #edf0ee;
  --text-secondary: #c4cbc8;
  --text-tertiary: #909a99;
  --text-disabled: #646d6e;
  --trace: #9ba9d2;
  --trace-hover: #afbbe0;
  --trace-pressed: #8292c0;
  --trace-soft: #333b57;
  --accent: #9ba9d2;
  --accent-hover: #afbbe0;
  --accent-pressed: #8292c0;
  --accent-soft: #333b57;
  --on-accent: #172033;
  --focus-ring: #b2bee1;
  --selected-row: #374158;
  --hover-row: #2b333c;
  --info: #83b2bc;
  --info-soft: #293e43;
  --team-a: #c4979b;
  --team-b: #83a8bd;
  --side-t: #c4979b;
  --side-ct: #83a8bd;
  --success: #8db295;
  --success-soft: #2c4032;
  --warning: #c4a271;
  --warning-soft: #433723;
  --danger: #d08794;
  --danger-hover: #de9aa5;
  --danger-soft: #472d34;
  --on-danger: #2a1015;
  --surface-shadow: 0 1px 0 rgba(255, 255, 255, .025), 0 10px 30px rgba(0, 0, 0, .25);
  --shadow-sheet: 0 20px 54px rgba(0, 0, 0, .43);
  --shadow-dialog: 0 30px 82px rgba(0, 0, 0, .53);
  --preset-chrome: #1a1f25;
  --preset-sidebar-art: radial-gradient(circle at 8% 8%, rgba(153, 132, 180, .11), transparent 28%), radial-gradient(circle at 92% 82%, rgba(117, 157, 145, .10), transparent 34%);
  --preset-primary: linear-gradient(135deg, #a7b3da, #7d8cbb);
  --preset-primary-border: #aebae0;
  --preset-active-ink: #c8d0ee;`,
  ),
];
