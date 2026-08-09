# Changelog

## 1.0.0 (2026-08-09)


### ⚠ BREAKING CHANGES

* Intel mac (osx-x64) builds are no longer published. Apple Silicon and Windows x64 are the supported downloads.

### Features

* automated Windows and macOS release builds ([da3b993](https://github.com/adanub/Hoard/commit/da3b9939bd27e7da3e581336ddf36fae3cfa6874))
* back up and sync a project's archive to another folder ([a60d3b5](https://github.com/adanub/Hoard/commit/a60d3b59a39f59e96f197772facb273b6ca6d457))
* **board:** export a board as a browsable folder tree of images ([b42a3e9](https://github.com/adanub/Hoard/commit/b42a3e9b568e4add53de5d47b5ce6014d90da02b))
* **board:** Sync stops early once it reaches images you already have ([b458da1](https://github.com/adanub/Hoard/commit/b458da1c1faa2a6c4c40fdd8b7099e9b22839452))
* **board:** the image details panel shows the pin id ([0746835](https://github.com/adanub/Hoard/commit/074683547871511a53f499e7278c1dc246240fe3))
* **core:** an image is one saved pin — re-import and recovery fixed ([8f4f5b1](https://github.com/adanub/Hoard/commit/8f4f5b1bf8e88bbaaec008f67416d084efcea555))
* **library:** export the whole project as folders in one run ([d70e7a3](https://github.com/adanub/Hoard/commit/d70e7a35d38f0a242e89d78b6da4194b1864cbcd))
* **library:** sync every board in a project from the board grid ([b6e786f](https://github.com/adanub/Hoard/commit/b6e786f2c75424fa703405f355c6ab5fd7ff4b33))
* op log rotates into sealed chapters, ready for cloud remotes ([65ca5da](https://github.com/adanub/Hoard/commit/65ca5da8aa2812152a3f5a715c90358548e0aaf6))
* project folders hold only the archive; new Verify files check ([1840a50](https://github.com/adanub/Hoard/commit/1840a50e3763aa4ff78053d732776ff45a97845f))
* projects open from any computer, including network drives ([9c1aeb1](https://github.com/adanub/Hoard/commit/9c1aeb12c4e34bf76683be43f0b1deeb56a8b619))
* releases target Windows x64 and Apple Silicon only ([090f4ac](https://github.com/adanub/Hoard/commit/090f4acf3479e8cc8e6b1cd148bac1bad5e314e7))
* **ui:** card grids fill the row width, like the masonry ([5eb5603](https://github.com/adanub/Hoard/commit/5eb56033f3acd5998ed31a17485234f3a5607d09))
* **ui:** cropped button labels scroll into view on hover ([cff106f](https://github.com/adanub/Hoard/commit/cff106fdfc65f0b105c504b6dd4df17ab97363cb))
* **ui:** Hoard's own scrollbars, dropdowns and tooltips replace Fluent ([63c9389](https://github.com/adanub/Hoard/commit/63c9389ed9fcb87b276239db0dc34f2001358536))


### Bug Fixes

* board sync repairs images whose files are missing or damaged ([2708632](https://github.com/adanub/Hoard/commit/270863290ba0e573ba8e03275ae237ac0ee9fda5))
* **board:** a re-synced board no longer shows re-saved pins twice ([171af30](https://github.com/adanub/Hoard/commit/171af304e77dfb724e27e60af235e21bfe0fbf05))
* **build:** gallery-dl is fetched automatically instead of by hand ([f7ab893](https://github.com/adanub/Hoard/commit/f7ab893040b612247a8ad4a1330376c64b4bcf97))
* delete works on macOS and no longer promises the recycle bin there ([405335f](https://github.com/adanub/Hoard/commit/405335fa7b1e3fa48327ff03d98ea8ab7dd674df))
* projects copied between computers no longer show every image as missing ([d03c5c0](https://github.com/adanub/Hoard/commit/d03c5c00224d0ec1c63ee5cb3048f644facd8505))
* sync replay can't regress newer state, plus pin-identity hardening ([2ab9bc4](https://github.com/adanub/Hoard/commit/2ab9bc47aa49679ba1235c24e76b8ff6930f79f2))
* **ui:** long button labels fade out instead of cropping at the edge ([c24ad00](https://github.com/adanub/Hoard/commit/c24ad00393768f5b1cf23be1b7e429daf2b21660))


### Performance

* **sync:** backup sync now moves only what changed ([e4e6fab](https://github.com/adanub/Hoard/commit/e4e6fabb1f3f058337e961e3f1fb04ceab3f0f3f))
