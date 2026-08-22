# Changelog

## [1.2.0](https://github.com/adanub/Hoard/compare/v1.1.2...v1.2.0) (2026-08-22)


### Features

* **import:** add Opera GX to the cookies browser list ([8cc1aa5](https://github.com/adanub/Hoard/commit/8cc1aa556714c94395531a74fa60d4a804d96919))
* **import:** warn when the chosen browser is holding its cookies ([91d9877](https://github.com/adanub/Hoard/commit/91d9877ea6d670da00af8a50019ac02e93d04cba))


### Bug Fixes

* **import:** report a locked cookie database instead of a missing board ([5e083c0](https://github.com/adanub/Hoard/commit/5e083c018ac3d5dc3a5c2a3df762b19cdda6d838))

## [1.1.2](https://github.com/adanub/Hoard/compare/v1.1.1...v1.1.2) (2026-08-22)


### Bug Fixes

* **settings:** show the app version without its build metadata hash ([4748c1f](https://github.com/adanub/Hoard/commit/4748c1f7683c25aa17e333a49760bcfb8c58136f))

## [1.1.1](https://github.com/adanub/Hoard/compare/v1.1.0...v1.1.1) (2026-08-16)


### Bug Fixes

* **board:** the last row of images is no longer cut off at the bottom ([775b062](https://github.com/adanub/Hoard/commit/775b062ff3d41f1572247e669da7957fcc44774a))
* **macos:** app contents draw at the right size after going fullscreen ([30d5e40](https://github.com/adanub/Hoard/commit/30d5e40519e0066d0d41c63e83f47b7dd849642a))

## [1.1.0](https://github.com/adanub/Hoard/compare/v1.0.0...v1.1.0) (2026-08-16)


### Features

* direct downloads on readme for easier access ([cce402b](https://github.com/adanub/Hoard/commit/cce402bdcfe1ada9de96a9976458e2717bc958c7))

## 1.0.0 (2026-08-16)


### ⚠ BREAKING CHANGES

* Intel mac (osx-x64) builds are no longer published. Apple Silicon and Windows x64 are the supported downloads.

### Features

* automated Windows and macOS release builds ([dae7f6b](https://github.com/adanub/Hoard/commit/dae7f6b2e81b3a2e6577c39e0f61823a4aec29c9))
* back up and sync a project's archive to another folder ([6fa8b30](https://github.com/adanub/Hoard/commit/6fa8b30a2272faa0915dca61decf53a1909287f1))
* **board:** export a board as a browsable folder tree of images ([3c43f67](https://github.com/adanub/Hoard/commit/3c43f676d3ffb2a2936d18e27f99cbf31f3da52f))
* **board:** Sync stops early once it reaches images you already have ([5b07018](https://github.com/adanub/Hoard/commit/5b07018bdefa6f9a6eec096a54970a736afbe417))
* **board:** the image details panel shows the pin id ([6b1f67c](https://github.com/adanub/Hoard/commit/6b1f67cefcb64adae0197eb2d6684a5d88d5e352))
* **core:** an image is one saved pin — re-import and recovery fixed ([4629c25](https://github.com/adanub/Hoard/commit/4629c25cbe319043d4e1f264f35bbb374c341e01))
* **library:** export the whole project as folders in one run ([c959b2a](https://github.com/adanub/Hoard/commit/c959b2ad16f0c6ab005d021e73944d3e6d7dce9b))
* **library:** sync every board in a project from the board grid ([f4e097a](https://github.com/adanub/Hoard/commit/f4e097a35c98bf6b610c3174a26d106e475dd297))
* op log rotates into sealed chapters, ready for cloud remotes ([eb1f63b](https://github.com/adanub/Hoard/commit/eb1f63b8f71833d4ee8ad95b6e7ce28f8f23ffe0))
* project folders hold only the archive; new Verify files check ([4a5c718](https://github.com/adanub/Hoard/commit/4a5c718137a1f8f80607cd1a7cd68c0ea789fc6d))
* projects open from any computer, including network drives ([19ca0bf](https://github.com/adanub/Hoard/commit/19ca0bff7fb5faac24f4a55f8ea3eb56a997dd41))
* releases target Windows x64 and Apple Silicon only ([a49b407](https://github.com/adanub/Hoard/commit/a49b407a48ae43c918c5ed67d4c586fa564cbad3))
* **shell:** a light/dark switch in the breadcrumb strip ([d887da5](https://github.com/adanub/Hoard/commit/d887da5182f5d25bf0fff42a5b4179db59ff1fc8))
* **ui:** card grids fill the row width, like the masonry ([a15e85e](https://github.com/adanub/Hoard/commit/a15e85efacf18a4c82651bcfb3b2a59ceca685ab))
* **ui:** cropped button labels scroll into view on hover ([181d776](https://github.com/adanub/Hoard/commit/181d776eeec8d0da8997d7ccdfbf04116ecbc4c2))
* **ui:** Hoard's own scrollbars, dropdowns and tooltips replace Fluent ([befaa55](https://github.com/adanub/Hoard/commit/befaa5525475d0171060a994599c89611bbab7fc))
* **ui:** toasts stay until dismissed and can expand to full details ([f8b2c78](https://github.com/adanub/Hoard/commit/f8b2c782c21987fa83fb0c34c1218897da7cb57d))
* **updates:** macOS updates itself, like the Windows installer build ([bbb2fb1](https://github.com/adanub/Hoard/commit/bbb2fb10fa4a6a596b6bee4747f31b55b28ef311))
* **updates:** Windows installer with opt-in automatic updates ([efcd81b](https://github.com/adanub/Hoard/commit/efcd81b1adce4cb7ed1feb79c0adea2d51651000))


### Bug Fixes

* **app:** Hoard shows up as "Hoard", not "Hoard.Desktop" ([feaa85a](https://github.com/adanub/Hoard/commit/feaa85a680fb817c7fe7f341c90e8827c4420a3b))
* **app:** the macOS app runs as "Hoard", not "Avalonia Application" ([f0bdfe3](https://github.com/adanub/Hoard/commit/f0bdfe3925965822e0b0480e1c6b3ec312d86d67))
* board sync repairs images whose files are missing or damaged ([51c642e](https://github.com/adanub/Hoard/commit/51c642ec2fed4305cb641e893e8f0a2ff7271f2c))
* **board:** a re-synced board no longer shows re-saved pins twice ([13512c5](https://github.com/adanub/Hoard/commit/13512c5f991034778b8696fcfd7676f197492ece))
* **build:** gallery-dl is fetched automatically instead of by hand ([4c6df9d](https://github.com/adanub/Hoard/commit/4c6df9d8907dd1c66eb6a373961a9dec45f42c69))
* delete works on macOS and no longer promises the recycle bin there ([ccf5025](https://github.com/adanub/Hoard/commit/ccf50252ed1e71a8f541cacf5351863acaf313a9))
* **import:** the cookie browser you pick is remembered for next time ([80d75ab](https://github.com/adanub/Hoard/commit/80d75ab0d00dd1ece8ebe1369a03d6965a2068b6))
* **lightbox:** close button no longer vanishes on hover in light mode ([76d8623](https://github.com/adanub/Hoard/commit/76d862379111f9c733b92ad66eadb6ecb0d1f5af))
* projects copied between computers no longer show every image as missing ([2ccf4c2](https://github.com/adanub/Hoard/commit/2ccf4c272a06f5058cdabf6fd52b3d222e2bea68))
* **shell:** breadcrumbs work while the fullscreen viewer is open ([e816c93](https://github.com/adanub/Hoard/commit/e816c93ff7409e96cc9bde962f4fd720a77e61eb))
* sync replay can't regress newer state, plus pin-identity hardening ([87389c4](https://github.com/adanub/Hoard/commit/87389c49cb13747e312201be76096c2e7daf9acd))
* **sync:** sync all boards shows progress and names what failed ([4528ebb](https://github.com/adanub/Hoard/commit/4528ebb16c2b4d9627409c5bc8c796d0ee3912ff))
* typos and clarity in readme ([09094b0](https://github.com/adanub/Hoard/commit/09094b044560ae7d21efcd9b0ca8ee45216e488c))
* **ui:** long button labels fade out instead of cropping at the edge ([3dbd2ec](https://github.com/adanub/Hoard/commit/3dbd2ec3c9020cd6d262faa36cc2fd3c86053837))


### Performance

* **sync:** backup sync now moves only what changed ([ece23fb](https://github.com/adanub/Hoard/commit/ece23fb10016e61b93a1a133057284119eba7480))


### Chores

* reset release state after history rewrite ([f882d71](https://github.com/adanub/Hoard/commit/f882d716657f8c9f5f7e8333d8714088d670faac))
* reset release version state after history rewrite ([5d1a44b](https://github.com/adanub/Hoard/commit/5d1a44bd6ba277f0f50e0142249ec9dbe3e97886))
* reset the release pipeline so the next release is 1.0.0 ([4656fe9](https://github.com/adanub/Hoard/commit/4656fe9b5356b83976529277f231bc6d6924ea5a))

## Changelog
