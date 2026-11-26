# Stage 10 — Scene Library and Portability

The Live Studio and Creator Studio compositor now includes a scene-management workspace.

- Save the current composition under a name; up to 40 named scenes are retained per studio in browser local storage.
- Load a saved scene without changing the other studio's scene library.
- Export/import a portable JSON scene file.
- Export the current Preview canvas as a 1920×1080 PNG snapshot.

Scene JSON stores composition settings and layer metadata, not the actual Media Box files. For portability, import the same source assets into Media Box on the destination browser/device first so asset IDs can resolve. Browser storage is local to the browser profile; use JSON export for backup or transfer.

The Program scene in Live Studio remains separate from Preview. Loading or editing a Preview scene does not go live until TAKE.
