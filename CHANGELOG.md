# Changelog

Each `## version` section becomes the notes of that release, and is what the
launcher shows in its "What's new" panel.

## 1.5.2

- Character creation: the class panel's border is now centred on the class icons.

## 1.5.1

- Character creation: the class panel and the customization column now have a border on their right side. Both run off the right edge of the screen in the original layout, so their right border never showed; the panels themselves are not moved. The customization column's new border sits over the last few pixels of its portraits.

## 1.5.0

- New setting (experimental): "Use the full width of ultrawide screens in the menus". The login, character list and character creation screens then fill a screen wider than 16:9 instead of keeping black bars at the sides. Off by default.
- The character creation fix for ultrawide screens from 1.4.1 now also applies to the 64-bit client (Wow-64.exe), which 1.4.2 skipped.

## 1.4.2

- Fixed a crash (ERROR #134, "Invalid function pointer") when opening the character creation screen, caused by the ultrawide fix in 1.4.1.

## 1.4.1

- Fixed the character creation screen on ultrawide (wider than 16:9) screens: the class panel and the customization column no longer spill a dark strip into the black bar on the right.

## 1.4.0

- Several accounts: every account you save in Settings, or log in with from the game, is remembered. Once there is more than one, the account under Play becomes a drop-down for choosing who to play as.
- Realms work the same way: once the game has been on more than one realm, the realm under Play becomes a drop-down, and the game goes to the one you choose.
- Settings has a "Forget this account" link to remove an account and its password.

## 1.3.0

- Auto-login: enter your account name and password in Settings and tick "Log in automatically". Play then logs you in once the game's login screen is up. The password is saved encrypted for your Windows account on this PC.
- Shows "Last played: ACCOUNT - REALM" under the Play button.
- An account name set in Settings is filled in on the game's login screen, with or without auto-login.

## 1.2.2

- The login music now starts at 10% volume instead of 50%. If you already moved the slider, your setting is kept.

## 1.2.1

- The ♪ button now opens a volume slider for the login music, with the on/off switch under it. The default is 50%.

## 1.2.0

- Plays the Mists of Pandaria login screen music, read from your own game files. The ♪ button turns it on and off.

## 1.1.0

- Blossom petals now drift off the tree and blow across the window in the wind.

## 1.0.0

First release.

- Starts your own Mists of Pandaria 5.4.8 client and patches it in memory to connect to the server. No game files are changed.
- Works with the 32-bit and 64-bit client.
- Updates itself from GitHub: when a new version is out, Play turns into Update.
