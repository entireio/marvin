# Marvin — Project website

Source for the public Marvin website, published with GitHub Pages.

> **Status: empty.** This folder is reserved. Nothing is built or deployed yet.

---

## What goes here

The public face of the project: what Marvin is, what it can do, how to build
one, and a gallery of the thing actually moving. The intent is a proper
designed site with scroll-driven animation — not a rendered README.

This is **not** the app you drive the robot with. That is
[`controller/`](../controller), and it stays a separate, dependency-free static
app so it can be served from anywhere.

Planned sections:

1. **Hero** — the robot, moving
2. **Anatomy** — an annotated walkthrough of the subsystems, revealed on scroll
3. **Build it** — printing, electronics, flashing, driving
4. **Docs** — the assembly guide and protocol reference
5. **Community** — issues, discussions, builds by other people

---

## Deployment

GitHub Pages can serve either the `docs/` folder of the default branch or a
branch built by an action. Nothing is configured yet; the choice depends on
whether the site ends up needing a build step.

If the site is built rather than hand-written, commit the **source** here and
publish the build output through a GitHub Actions workflow — do not commit
`dist/`. [`.gitignore`](../.gitignore) already excludes `docs/dist/`,
`docs/node_modules/`, and `docs/.cache/`.

A static site with no build step also needs an empty `.nojekyll` file at the
root of whatever gets published, or GitHub Pages will silently drop any file or
folder whose name starts with an underscore.

---

## Notes for whoever builds this

- Media is heavy. Video and large images should go through Git LFS — `*.png`,
  `*.jpg`, and `*.mp4` are already configured in
  [`.gitattributes`](../.gitattributes) — or be hosted externally and linked.
- Scroll animation should degrade gracefully. Honour
  `prefers-reduced-motion`, and make sure the content is readable and complete
  with JavaScript disabled.
- Anything factual — the pin map, the command protocol, the parts list — should
  be linked to its home in the repository rather than restated here. Duplicated
  documentation goes stale, and the website is the copy people will trust.

---

## Licence

The website source is covered by the project's [MIT Licence](../LICENSE).
