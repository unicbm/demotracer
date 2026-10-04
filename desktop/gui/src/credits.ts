/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

export type CreditFoundationId = "xbribo" | "ianLucas" | "demoparser" | "csgowiki";

export interface CreditPerson {
  name: string;
  githubHandle: string;
  avatarUrl: string;
  profileUrl: string;
}

export interface CreditProject {
  name: string;
  repository: string;
  url: string;
}

export interface CreditFoundation {
  id: CreditFoundationId;
  author: string;
  githubHandle: string;
  avatarUrl: string;
  profileUrl: string;
  projects: readonly CreditProject[];
}

export const DEMOTRACER_CREDITS = {
  creator: {
    name: "unicbm",
    githubHandle: "unicbm",
    avatarUrl: "https://avatars.githubusercontent.com/u/136828391?v=4",
    profileUrl: "https://github.com/unicbm",
  } satisfies CreditPerson,
  contributors: [
    {
      name: "ed0ard",
      githubHandle: "ed0ard",
      avatarUrl: "https://avatars.githubusercontent.com/u/267400231?v=4",
      profileUrl: "https://github.com/ed0ard",
    },
    {
      name: "Newbie046",
      githubHandle: "Newbie046",
      avatarUrl: "https://avatars.githubusercontent.com/u/268993736?v=4",
      profileUrl: "https://github.com/Newbie046",
    },
    {
      name: "XBribo",
      githubHandle: "XBribo",
      avatarUrl: "https://avatars.githubusercontent.com/u/63445088?v=4",
      profileUrl: "https://github.com/XBribo",
    },
    {
      name: "Misaka17032",
      githubHandle: "Misaka17032",
      avatarUrl: "https://avatars.githubusercontent.com/u/40137262?v=4",
      profileUrl: "https://github.com/Misaka17032",
    },
    {
      name: "T1mLuk0",
      githubHandle: "T1mLuk0",
      avatarUrl: "https://avatars.githubusercontent.com/u/287299398?v=4",
      profileUrl: "https://github.com/T1mLuk0",
    },
  ] satisfies readonly CreditPerson[],
  foundations: [
    {
      id: "xbribo",
      author: "XBribo",
      githubHandle: "XBribo",
      avatarUrl: "https://avatars.githubusercontent.com/u/63445088?v=4",
      profileUrl: "https://github.com/XBribo",
      projects: [
        {
          name: "CS2 Bot Controller",
          repository: "XBribo/CS2-Bot-Controller",
          url: "https://github.com/XBribo/CS2-Bot-Controller",
        },
        {
          name: "CS2 Bot Hider",
          repository: "XBribo/CS2-Bot-Hider",
          url: "https://github.com/XBribo/CS2-Bot-Hider",
        },
      ],
    },
    {
      id: "ianLucas",
      author: "Ian Lucas",
      githubHandle: "ianlucas",
      avatarUrl: "https://avatars.githubusercontent.com/u/9924503?v=4",
      profileUrl: "https://github.com/ianlucas",
      projects: [
        {
          name: "CS2 Inventory Simulator",
          repository: "ianlucas/cs2-inventory-simulator",
          url: "https://github.com/ianlucas/cs2-inventory-simulator",
        },
        {
          name: "cs2-lib",
          repository: "ianlucas/cs2-lib",
          url: "https://github.com/ianlucas/cs2-lib",
        },
        {
          name: "cs2-lib-inspect",
          repository: "ianlucas/cs2-lib-inspect",
          url: "https://github.com/ianlucas/cs2-lib-inspect",
        },
      ],
    },
    {
      id: "demoparser",
      author: "LaihoE & demoparser contributors",
      githubHandle: "LaihoE",
      avatarUrl: "https://avatars.githubusercontent.com/u/80683769?v=4",
      profileUrl: "https://github.com/LaihoE",
      projects: [
        {
          name: "demoparser",
          repository: "LaihoE/demoparser",
          url: "https://github.com/LaihoE/demoparser",
        },
      ],
    },
    {
      id: "csgowiki",
      author: "CSGOWiki",
      githubHandle: "csgowiki",
      avatarUrl: "https://avatars.githubusercontent.com/u/103179080?v=4",
      profileUrl: "https://github.com/csgowiki",
      projects: [
        {
          name: "Mini Demo Encoder",
          repository: "csgowiki/minidemo-encoder",
          url: "https://github.com/csgowiki/minidemo-encoder",
        },
      ],
    },
  ] satisfies readonly CreditFoundation[],
} as const;
