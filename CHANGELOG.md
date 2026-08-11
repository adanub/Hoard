# Changelog

## 1.0.0 (2026-08-11)


### ⚠ BREAKING CHANGES

* Intel mac (osx-x64) builds are no longer published. Apple Silicon and Windows x64 are the supported downloads.

### Features

* automated Windows and macOS release builds ([4789e80](https://github.com/adanub/Hoard/commit/4789e80f7a2d1c55095e36da297a4c9ee716e121))
* back up and sync a project's archive to another folder ([e0d46b4](https://github.com/adanub/Hoard/commit/e0d46b4fa9cd66b633c22db3e79de1faa129843b))
* **board:** export a board as a browsable folder tree of images ([7f422a2](https://github.com/adanub/Hoard/commit/7f422a20f4b8c374830e9b16e176e45202b8d21c))
* **board:** Sync stops early once it reaches images you already have ([ad60703](https://github.com/adanub/Hoard/commit/ad60703d569fbb989ee6383d716df0c70ccdd140))
* **board:** the image details panel shows the pin id ([6c6206d](https://github.com/adanub/Hoard/commit/6c6206d812e8ca8fa3ec4cfc90cd6458146ec753))
* **core:** an image is one saved pin — re-import and recovery fixed ([5b60def](https://github.com/adanub/Hoard/commit/5b60def1597ee7f068738a5ef12400f1ede10d4e))
* **library:** export the whole project as folders in one run ([b21f57e](https://github.com/adanub/Hoard/commit/b21f57e702cb3e1196bdbe98196f1ebee9d697b8))
* **library:** sync every board in a project from the board grid ([b00dd59](https://github.com/adanub/Hoard/commit/b00dd59ea801d46bcb52fb0023e2c8f6c71a6941))
* op log rotates into sealed chapters, ready for cloud remotes ([69a52f8](https://github.com/adanub/Hoard/commit/69a52f85bfbfb6fffcdf120491a98dc7f32cbe55))
* project folders hold only the archive; new Verify files check ([3f70186](https://github.com/adanub/Hoard/commit/3f70186e0e2698c3686c9a9a3f1f30c4a4865c6b))
* projects open from any computer, including network drives ([476d87b](https://github.com/adanub/Hoard/commit/476d87b12d065eeb753fdd3c32582dd944f392c8))
* releases target Windows x64 and Apple Silicon only ([9786e4b](https://github.com/adanub/Hoard/commit/9786e4b7c3242550f64d1e080e8a246ba690274e))
* **ui:** card grids fill the row width, like the masonry ([17e3129](https://github.com/adanub/Hoard/commit/17e31293d03ba0ed84a651c327cca1db7c0b787e))
* **ui:** cropped button labels scroll into view on hover ([660af0c](https://github.com/adanub/Hoard/commit/660af0c4fc27bdc03f163ba846c52e701da51f6c))
* **ui:** Hoard's own scrollbars, dropdowns and tooltips replace Fluent ([bfb8622](https://github.com/adanub/Hoard/commit/bfb862293e6a19f77892b84126d8bd840fd581d6))


### Bug Fixes

* board sync repairs images whose files are missing or damaged ([110531e](https://github.com/adanub/Hoard/commit/110531e10404c857cc1f10a357934271f842b20d))
* **board:** a re-synced board no longer shows re-saved pins twice ([c976c0c](https://github.com/adanub/Hoard/commit/c976c0c1e6d9b7bcfe731a79a520f1236b365d4c))
* **build:** gallery-dl is fetched automatically instead of by hand ([2c86be3](https://github.com/adanub/Hoard/commit/2c86be39e5bfad8707e42c52242a87201d7956d5))
* delete works on macOS and no longer promises the recycle bin there ([7d00511](https://github.com/adanub/Hoard/commit/7d005116173a730354fbad49329b652334bb9fb3))
* projects copied between computers no longer show every image as missing ([2d173a4](https://github.com/adanub/Hoard/commit/2d173a4d2dc5a15f112f0fd53503c235344f3e37))
* sync replay can't regress newer state, plus pin-identity hardening ([71d04ee](https://github.com/adanub/Hoard/commit/71d04ee272d3f9e00ae20bfefbd244681731fa74))
* typos and clarity in readme ([7ca3517](https://github.com/adanub/Hoard/commit/7ca3517319b6a4bebf1a0543e6f4052168d177aa))
* **ui:** long button labels fade out instead of cropping at the edge ([20371ff](https://github.com/adanub/Hoard/commit/20371ffeccab843561d9450fba68c3e162f0f6f5))


### Performance

* **sync:** backup sync now moves only what changed ([6d35525](https://github.com/adanub/Hoard/commit/6d35525836d0982b75ca612f0b33d44b94c033a4))


### Chores

* reset release state after history rewrite ([6faa16d](https://github.com/adanub/Hoard/commit/6faa16d63ce7768a646151a2f9029d2c54553fde))
* reset release version state after history rewrite ([abc34f9](https://github.com/adanub/Hoard/commit/abc34f9a742e2e5382e63c07996fc1b56f537077))
