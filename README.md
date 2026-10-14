# Enzisoft Packages

Editor window for installing and updating the Enzisoft packages you own from `https://upm.enzisoft.com`.

Each package is saved as `Packages/<package-name>.tgz` and added to `Packages/manifest.json` as a relative
`file:<package-name>.tgz` dependency. Commit these archives with the project. The project then keeps working without
this manager, without signing in and without the registry; no scoped registry is needed.

## Requirements

Unity 6000.0 or newer. No other Enzisoft package is required; Unity's Newtonsoft Json package is installed with it.

## Setup

1. In **Window > Package Management > Package Manager**, click **+**, choose **Install package from git URL…** and enter
   `https://github.com/enzi/com.enzisoft.packages.git`. Append a tag (`…/com.enzisoft.packages.git#v0.1.1`) to pin a
   version. Git must be installed.
2. Open **Window > Package Management > Enzisoft Packages**.
3. Click **Sign in**. The browser opens; sign in with Google, GitHub or an emailed link to your purchase address and allow
   the Editor. Each Editor gets its own token, stored in your user's Editor preferences, never in the project. Free
   packages need a sign-in too; without a purchase, sign in with Google or GitHub.

The manager is listed in its own window as well: **Switch to .tgz** vendors it like any other package, and later versions
then arrive as **Update**.

## Usage

- **Install** / **Update** installs the newest version your purchase covers. Missing `com.enzisoft.*` dependencies are
  installed alongside at the version they require. Warnings, such as an outdated folder copy of a dependency, go to
  the Console.
- **Switch to .tgz** switches a package resolved from git or a scoped registry to a local archive of the same version.
- **Remove** removes a package this manager installed and deletes its archive. It is refused while another installed
  package still depends on it.

Packages present as folders (embedded under `Packages/` or `file:` folder dependencies) are never changed.

Every download is checked against the registry's sha512 hash before it is written. If Unity rejects the change, the
previous archives are restored.
