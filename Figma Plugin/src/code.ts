import { getLayout, hasChildren, isGroupNode, fastClone, traverseLayers, helpers } from "./helpers";
// import { helpers as _helpers } from "libx.js/build/helpers";

const settings = {
	ui: {
		baseHeight: 670,
		baseWidth: 370,
	},
};

const isCodegenMode = figma.editorType === "dev" && figma.mode === "codegen";
const shouldShowUi = !isCodegenMode;
const unityPackageAssetLimitPerScreen = 120;
const unityPackageSizeLimit = 35 * 1024 * 1024;

function slugTokens(name: string): string[] {
	return (name || "")
		.toLowerCase()
		.replace(/&/g, " and ")
		.replace(/[^a-z0-9]+/g, " ")
		.trim()
		.split(/\s+/)
		.filter((token) => token && !/^\d+$/.test(token));
}

function commonPrefixLength(a: string[], b: string[]): number {
	const length = Math.min(a.length, b.length);
	let index = 0;
	while (index < length && a[index] === b[index]) {
		index++;
	}
	return index;
}

function getExportNameBase(nodes: readonly SceneNode[]): string {
	const names = nodes.map((node: any) => node?.name || "").filter(Boolean);
	const tokenLists = names.map(slugTokens).filter((tokens) => tokens.length > 0);

	if (tokenLists.length === 0) {
		return "selection";
	}

	const allCommonLength = tokenLists.reduce(
		(length, tokens) => Math.min(length, commonPrefixLength(tokenLists[0], tokens)),
		tokenLists[0].length
	);
	if (allCommonLength > 0) {
		return tokenLists[0].slice(0, allCommonLength).join("-");
	}

	const prefixScores = new Map<string, { count: number; length: number }>();
	for (const tokens of tokenLists) {
		for (let length = 1; length <= tokens.length; length++) {
			const prefix = tokens.slice(0, length).join("-");
			const previous = prefixScores.get(prefix) || { count: 0, length };
			previous.count++;
			prefixScores.set(prefix, previous);
		}
	}

	let bestPrefix = "";
	let bestScore = -1;
	for (const [prefix, value] of prefixScores) {
		if (value.count < 2) {
			continue;
		}
		const score = value.count * 100 + value.length;
		if (score > bestScore || (score === bestScore && prefix.length > bestPrefix.length)) {
			bestPrefix = prefix;
			bestScore = score;
		}
	}

	if (bestPrefix) {
		return bestPrefix;
	}

	return tokenLists[0].slice(0, 4).join("-") || "selection";
}

function getExportFileName(nodes: readonly SceneNode[]): string {
	const stamp = new Date().toISOString().replace(/[:.]/g, "-");
	return `figma-${getExportNameBase(nodes)}-${stamp}.json`;
}

const allPropertyNames = [
	"id",
	"width",
	"height",
	"currentPage",
	"cancel",
	"origin",
	"onmessage",
	"center",
	"zoom",
	"fontName",
	"name",
	"visible",
	"locked",
	"constraints",
	"relativeTransform",
	"x",
	"y",
	"rotation",
	"targetAspectRatio",
	"layoutAlign",
	"layoutGrow",
	"opacity",
	"blendMode",
	"isMask",
	"effects",
	"effectStyleId",
	"expanded",
	"backgrounds",
	"backgroundStyleId",
	"fills",
	"strokes",
	"strokeWeight",
	"strokeMiterLimit",
	"strokeAlign",
	"strokeCap",
	"strokeJoin",
	"dashPattern",
	"fillStyleId",
	"strokeStyleId",
	"cornerRadius",
	"cornerSmoothing",
	"topLeftRadius",
	"topRightRadius",
	"bottomLeftRadius",
	"bottomRightRadius",
	"exportSettings",
	"overflowDirection",
	"numberOfFixedChildren",
	"description",
	"layoutMode",
	"primaryAxisSizingMode",
	"counterAxisSizingMode",
	"primaryAxisAlignItems",
	"counterAxisAlignItems",
	"paddingLeft",
	"paddingRight",
	"paddingTop",
	"paddingBottom",
	"itemSpacing",
	"layoutGrids",
	"gridStyleId",
	"clipsContent",
	"guides",
	"guides",
	"selection",
	"selectedTextRange",
	"backgrounds",
	"arcData",
	"pointCount",
	"pointCount",
	"innerRadius",
	"vectorNetwork",
	"vectorPaths",
	"handleMirroring",
	"textAlignHorizontal",
	"textAlignVertical",
	"textAutoResize",
	"paragraphIndent",
	"paragraphSpacing",
	"autoRename",
	"textStyleId",
	"fontSize",
	"fontName",
	"textCase",
	"textDecoration",
	"letterSpacing",
	"lineHeight",
	"characters",
	// "mainComponent",
	"scaleFactor",
	"booleanOperation",
	"expanded",
	"name",
	"type",
	"paints",
	"type",
	"fontSize",
	"textDecoration",
	"fontName",
	"letterSpacing",
	"lineHeight",
	"paragraphIndent",
	"paragraphSpacing",
	"textCase",
	"type",
	"effects",
	"type",
	"layoutGrids",
	"absoluteRenderBounds",
	"absoluteBoundingBox",
];

// The Figma nodes are hard to inspect at a glance because almost all properties are non enumerable
// getters. This removes that wrapping for easier inspecting
const cloneObject = (obj: any, valuesSet = new Set()) => {
	if (!obj || typeof obj !== "object") {
		return obj;
	}

	const newObj: any = Array.isArray(obj) ? [] : {};

	for (const property of allPropertyNames) {
		const value = obj[property];
		if (value !== undefined && typeof value !== "symbol") {
			newObj[property] = obj[property];
		}
	}

	return newObj;
};

async function postSelection() {
	// Only send selection updates if UI is available
	try {
		if (figma.ui) {
			figma.ui.postMessage({
				type: "selectionChange",
				elements: figma.currentPage.selection.map((el: any) => ({
					id: el.id,
					name: el.name,
					type: el.type,
					width: el.width,
					height: el.height,
				})),
			});
		}
	} catch (error) {
		// UI not available (happens in codegen mode), silently continue
		console.log("Selection change: UI not available");
	}
}

// Only listen to selection changes when the plugin UI is available.
if (shouldShowUi) {
	figma.on("selectionchange", async () => {
		postSelection();
	});
}

// Show UI when plugin is launched normally, not during Dev Mode codegen.
if (shouldShowUi) {
	figma.showUI(__html__, {
		width: settings.ui.baseWidth,
		height: settings.ui.baseHeight,
	});
}
async function processImages(layer: RectangleNode | TextNode) {
	const images = getImageFills(layer);
	return (
		images &&
		Promise.all(
			images.map(async (image: any) => {
				if (image && image.intArr) {
					image.imageHash = await figma.createImage(image.intArr).hash;
					delete image.intArr;
				}
			})
		)
	);
}

function getImageFills(layer: RectangleNode | TextNode) {
	const images =
		Array.isArray(layer.fills) &&
		layer.fills.filter((item) => item.type === "IMAGE");
	return images;
}

const normalizeName = (str: string) =>
	str.toLowerCase().replace(/[^a-z]/gi, "");

const defaultFont = { family: "Roboto", style: "Regular" };

function sanitizeAssetName(name: string): string {
	return (name || "figma-node")
		.replace(/[^a-z0-9._-]+/gi, "_")
		.replace(/_+/g, "_")
		.replace(/^_+|_+$/g, "")
		.slice(0, 80) || "figma-node";
}

function bytesToBase64(bytes: Uint8Array): string {
	const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
	let output = "";
	let index = 0;

	for (; index + 2 < bytes.length; index += 3) {
		const triplet = (bytes[index] << 16) | (bytes[index + 1] << 8) | bytes[index + 2];
		output += chars[(triplet >> 18) & 63];
		output += chars[(triplet >> 12) & 63];
		output += chars[(triplet >> 6) & 63];
		output += chars[triplet & 63];
	}

	if (index < bytes.length) {
		const remaining = bytes.length - index;
		const triplet = (bytes[index] << 16) | (remaining === 2 ? bytes[index + 1] << 8 : 0);
		output += chars[(triplet >> 18) & 63];
		output += chars[(triplet >> 12) & 63];
		output += remaining === 2 ? chars[(triplet >> 6) & 63] : "=";
		output += "=";
	}

	return output;
}

function hashBytes(bytes: Uint8Array): string {
	let hash = 2166136261;
	for (let index = 0; index < bytes.length; index++) {
		hash ^= bytes[index];
		hash = Math.imul(hash, 16777619);
	}

	return (hash >>> 0).toString(16).padStart(8, "0");
}

function getNodeBox(node: any) {
	const box = node.absoluteBoundingBox || node.absoluteRenderBounds || node;
	return {
		x: box.x || 0,
		y: box.y || 0,
		width: Math.max(1, box.width || node.width || 1),
		height: Math.max(1, box.height || node.height || 1),
	};
}

function getRenderBox(node: any) {
	const box = node.absoluteRenderBounds || node.absoluteBoundingBox || node;
	return {
		x: box.x || 0,
		y: box.y || 0,
		width: Math.max(1, box.width || node.width || 1),
		height: Math.max(1, box.height || node.height || 1),
	};
}

function unionBoxes(boxes: Array<{ x: number; y: number; width: number; height: number }>) {
	let x1 = Math.min(...boxes.map((box) => box.x));
	let y1 = Math.min(...boxes.map((box) => box.y));
	let x2 = Math.max(...boxes.map((box) => box.x + box.width));
	let y2 = Math.max(...boxes.map((box) => box.y + box.height));
	return { x: x1, y: y1, width: x2 - x1, height: y2 - y1 };
}

function getFirstSolidFill(node: any) {
	const fills = Array.isArray(node.fills) ? node.fills : [];
	for (const fill of fills) {
		if (fill.type !== "SOLID" || fill.visible === false || !fill.color) continue;
		return {
			r: fill.color.r ?? 1,
			g: fill.color.g ?? 1,
			b: fill.color.b ?? 1,
			a: (fill.color.a ?? 1) * (fill.opacity ?? 1),
		};
	}

	return null;
}

function getPaintInfo(node: any) {
	const fills = Array.isArray(node.fills) ? node.fills : [];
	for (const fill of fills) {
		if (fill.visible === false) continue;
		if (fill.type === "SOLID" && fill.color) {
			return {
				kind: "solid",
				color: {
					r: fill.color.r ?? 1,
					g: fill.color.g ?? 1,
					b: fill.color.b ?? 1,
					a: (fill.color.a ?? 1) * (fill.opacity ?? 1),
				},
			};
		}

		if (String(fill.type).startsWith("GRADIENT") && Array.isArray(fill.gradientStops)) {
			return {
				kind: "gradient",
				gradientType: fill.type,
				stops: fill.gradientStops.map((stop: any) => ({
					position: stop.position ?? 0,
					color: {
						r: stop.color?.r ?? 1,
						g: stop.color?.g ?? 1,
						b: stop.color?.b ?? 1,
						a: (stop.color?.a ?? 1) * (fill.opacity ?? 1),
					},
				})),
			};
		}
	}

	return { kind: "solid", color: { r: 0, g: 0, b: 0, a: 1 } };
}

function getImageFill(node: any) {
	const fills = Array.isArray(node.fills) ? node.fills : [];
	for (const fill of fills) {
		if (fill.type === "IMAGE" && fill.visible !== false) {
			return fill;
		}
	}

	return null;
}

function hasTextDescendant(node: SceneNode): boolean {
	if (node.type === "TEXT") return true;
	if (!hasChildren(node)) return false;
	return node.children.some((child: SceneNode) => child.visible && hasTextDescendant(child));
}

function hasImageFillDescendant(node: SceneNode): boolean {
	if (getImageFill(node)) return true;
	if (!hasChildren(node)) return false;
	return node.children.some((child: SceneNode) => child.visible && hasImageFillDescendant(child));
}

function hasGraphicDescendant(node: SceneNode): boolean {
	if (!hasChildren(node)) return false;
	return node.children.some((child: SceneNode) => {
		if (!child.visible) return false;
		if (child.type !== "TEXT" && child.type !== "GROUP" && child.type !== "FRAME" && child.type !== "COMPONENT" && child.type !== "INSTANCE") {
			return true;
		}
		return hasGraphicDescendant(child);
	});
}

function getTextDescendantContent(node: SceneNode): string {
	if ((node as any).type === "TEXT") return (node as any).characters || "";
	if (!hasChildren(node)) return "";
	return node.children.map((child: SceneNode) => getTextDescendantContent(child)).join(" ").trim();
}

function getFirstTextDescendant(node: SceneNode): any | null {
	if ((node as any).type === "TEXT") return node as any;
	if (!hasChildren(node)) return null;

	for (const child of node.children) {
		if (!child.visible) continue;
		const found = getFirstTextDescendant(child);
		if (found) return found;
	}

	return null;
}

function countTextDescendants(node: SceneNode): number {
	if ((node as any).type === "TEXT") return 1;
	if (!hasChildren(node)) return 0;
	return node.children.reduce((count: number, child: SceneNode) => {
		return count + (child.visible ? countTextDescendants(child) : 0);
	}, 0);
}

function hasRotatedTextDescendant(node: SceneNode): boolean {
	if ((node as any).type === "TEXT") return Math.abs((node as any).rotation || 0) > 2;
	if (!hasChildren(node)) return false;
	return node.children.some((child: SceneNode) => child.visible && hasRotatedTextDescendant(child));
}

function hasExplicitButtonDescendant(node: SceneNode): boolean {
	if (!hasChildren(node)) return false;
	return node.children.some((child: SceneNode) => {
		if (!child.visible) return false;
		const childName = normalizeText((child as any).name || "");
		if (/\b(button|btn|cta|action|tap|press)\b/.test(childName)) return true;
		return hasExplicitButtonDescendant(child);
	});
}

function countImageFillDescendants(node: SceneNode): number {
	let count = getImageFill(node) ? 1 : 0;
	if (!hasChildren(node)) return count;
	return node.children.reduce((total: number, child: SceneNode) => {
		return total + (child.visible ? countImageFillDescendants(child) : 0);
	}, count);
}

function isLogoLikeGroup(node: any): boolean {
	if (!hasChildren(node) || !hasTextDescendant(node)) return false;
	if (hasImageFillDescendant(node)) return false;

	const name = String(node.name || "").toLowerCase();
	if (name.includes("logo") || name.includes("brand")) return true;

	const text = getTextDescendantContent(node).replace(/\s+/g, " ").trim();
	if (!text || text.length > 28) return false;

	const letters = text.replace(/[^a-z]/gi, "");
	const mostlyUppercase = letters.length >= 3 && letters === letters.toUpperCase();
	return mostlyUppercase && hasGraphicDescendant(node);
}

function normalizeText(value: string): string {
	return String(value || "").toLowerCase().replace(/[_-]+/g, " ").replace(/\s+/g, " ").trim();
}

function hasExplicitButtonName(node: any): boolean {
	const name = normalizeText(node.name);
	return /\b(button|btn|cta|action|tap|press)\b/.test(name);
}

function hasExplicitInputName(node: any): boolean {
	const name = normalizeText(node.name);
	return /\b(input|inpt|inp|field|textfield|txtfield|txt field|text field|form field|textarea|text area)\b/.test(name);
}

function hasExplicitDropdownName(node: any): boolean {
	const name = normalizeText(node.name);
	return /\b(dropdown|drop down|dd|ddl|select|selector|combo box|combobox|picker|choose|option|options|menu list|menu)\b/.test(name);
}

function hasExplicitSliderName(node: any): boolean {
	const name = normalizeText(node.name);
	return /\b(slider|sldr|slide|range|seek|scrub|progress|volume|brightness|opacity|amount|level|timeline|meter|bar)\b/.test(name);
}

function looksLikeButtonComponent(node: any): boolean {
	const name = normalizeText(node.name);
	const text = normalizeText(getTextDescendantContent(node));
	if (!text && !name) return false;
	if (hasExplicitButtonName(node)) return true;
	if (text.length > 36) return false;
	if (countTextDescendants(node) > 2) return false;

	const combined = `${name} ${text}`;
	return /\b(next|submit|continue|ready|explore|click|start|launch|capture|back|close|ok|done|play|menu|retry|home|skip|yes|no|save|send|login|sign in|sign up)\b/.test(combined) ||
		name.includes("project item") ||
		name.includes("project card");
}

function looksLikeInputComponent(node: any): boolean {
	const name = normalizeText(node.name);
	const text = normalizeText(getTextDescendantContent(node));
	const combined = `${name} ${text}`;

	if (hasExplicitInputName(node)) return true;
	if (countTextDescendants(node) > 2) return false;

	return /(enter|type).{0,20}(name|email|message|thought|text|phone|number)|your name|your email|enter name|enter email|please fill|placeholder/.test(combined);
}

function looksLikeDropdownComponent(node: any): boolean {
	const name = normalizeText(node.name);
	const text = normalizeText(getTextDescendantContent(node));
	const combined = `${name} ${text}`;
	if (!combined.trim()) return false;
	if (hasExplicitDropdownName(node)) return true;
	if (countTextDescendants(node) > 4) return false;

	return /\b(dropdown|drop down|dd|ddl|select|selector|combo box|combobox|picker|choose|option|options|menu list|menu)\b/.test(combined) ||
		/\b(select|choose)\b.{0,24}\b(type|category|country|city|option|item|value)\b/.test(combined);
}

function looksLikeSliderComponent(node: any): boolean {
	const name = normalizeText(node.name);
	const text = normalizeText(getTextDescendantContent(node));
	const combined = `${name} ${text}`;
	const box = getNodeBox(node);
	const isTrackLike = box.width >= box.height * 2.5 || box.height >= box.width * 2.5;

	if (hasExplicitSliderName(node)) return true;
	return /\b(slider|sldr|slide|range|seek|scrub|progress|volume|brightness|opacity|amount|level|timeline|meter|bar)\b/.test(combined) ||
		(isTrackLike && /\b(handle|thumb|track|knob)\b/.test(combined));
}

function shouldBakeTextIntoVisual(node: SceneNode): boolean {
	if (!hasChildren(node) || !hasTextDescendant(node) || !hasImageFillDescendant(node)) return false;
	const box = getNodeBox(node);
	const textCount = countTextDescendants(node);
	const imageCount = countImageFillDescendants(node);
	const name = normalizeText((node as any).name || "");
	if (hasExplicitButtonDescendant(node)) return false;

	if (hasRotatedTextDescendant(node) && textCount <= 4) return true;
	if (imageCount > 0 && textCount <= 4 && /\b(card|tile|item|project|marquee|gallery|strip)\b/.test(name)) return true;

	return box.width > box.height * 1.8 && imageCount > 0 && textCount <= 3;
}

function shouldBakeExplicitButton(node: SceneNode): boolean {
	if (!hasExplicitButtonName(node)) return false;
	if (!hasTextDescendant(node)) return true;
	if (hasRotatedTextDescendant(node)) return true;
	if (hasImageFillDescendant(node)) return true;
	if (hasGraphicDescendant(node) && countTextDescendants(node) <= 4) return true;
	return true;
}

function boxCenter(box: { x: number; y: number; width: number; height: number }) {
	return { x: box.x + box.width * 0.5, y: box.y + box.height * 0.5 };
}

function boxUnion(a: { x: number; y: number; width: number; height: number }, b: { x: number; y: number; width: number; height: number }) {
	const x1 = Math.min(a.x, b.x);
	const y1 = Math.min(a.y, b.y);
	const x2 = Math.max(a.x + a.width, b.x + b.width);
	const y2 = Math.max(a.y + a.height, b.y + b.height);
	return { x: x1, y: y1, width: x2 - x1, height: y2 - y1 };
}

function collectVisualDescendants(node: SceneNode, result: SceneNode[] = []): SceneNode[] {
	if (!node.visible || (node as any).type === "TEXT") return result;
	if (hasOwnVisual(node) || getImageFill(node)) result.push(node);
	if (hasChildren(node)) {
		for (const child of node.children) collectVisualDescendants(child, result);
	}
	return result;
}

function getSliderPartsFromCandidates(candidates: SceneNode[]) {
	const visible = candidates.filter((candidate) => candidate.visible && (candidate as any).type !== "TEXT");
	if (visible.length < 2 || visible.length > 8) return null;

	let best: any = null;
	for (const track of visible) {
		const trackBox = getNodeBox(track);
		const horizontal = trackBox.width >= trackBox.height * 3;
		const vertical = trackBox.height >= trackBox.width * 3;
		if (!horizontal && !vertical) continue;

		for (const handle of visible) {
			if (handle === track) continue;
			const handleBox = getNodeBox(handle);
			const trackCenter = boxCenter(trackBox);
			const handleCenter = boxCenter(handleBox);
			const union = boxUnion(trackBox, handleBox);
			const unionTrackish = horizontal ? union.width >= union.height * 2.2 : union.height >= union.width * 2.2;
			if (!unionTrackish) continue;

			let score = 0;
			if (horizontal) {
				const nearTrack = Math.abs(handleCenter.y - trackCenter.y) <= Math.max(trackBox.height, handleBox.height) * 1.2;
				const inRange = handleCenter.x >= trackBox.x - handleBox.width && handleCenter.x <= trackBox.x + trackBox.width + handleBox.width;
				const handleIsHandle = handleBox.width <= trackBox.width * 0.35 && handleBox.height >= trackBox.height * 1.2;
				if (!nearTrack || !inRange || !handleIsHandle) continue;
				score = trackBox.width / Math.max(1, trackBox.height) + handleBox.height / Math.max(1, trackBox.height);
			} else {
				const nearTrack = Math.abs(handleCenter.x - trackCenter.x) <= Math.max(trackBox.width, handleBox.width) * 1.2;
				const inRange = handleCenter.y >= trackBox.y - handleBox.height && handleCenter.y <= trackBox.y + trackBox.height + handleBox.height;
				const handleIsHandle = handleBox.height <= trackBox.height * 0.35 && handleBox.width >= trackBox.width * 1.2;
				if (!nearTrack || !inRange || !handleIsHandle) continue;
				score = trackBox.height / Math.max(1, trackBox.width) + handleBox.width / Math.max(1, trackBox.width);
			}

			if (!best || score > best.score) {
				const value = horizontal
					? Math.max(0, Math.min(1, (handleCenter.x - trackBox.x) / Math.max(1, trackBox.width)))
					: Math.max(0, Math.min(1, 1 - ((handleCenter.y - trackBox.y) / Math.max(1, trackBox.height))));
				best = { track, handle, orientation: horizontal ? "horizontal" : "vertical", box: union, value, score };
			}
		}
	}

	return best;
}

function getSliderParts(node: SceneNode) {
	const candidates = collectVisualDescendants(node).filter((candidate) => candidate !== node);
	return getSliderPartsFromCandidates(candidates);
}

function looksLikeVisualSliderComponent(node: SceneNode): boolean {
	if (!hasChildren(node)) return false;
	const parts = getSliderParts(node);
	if (!parts) return false;
	const nameHint = looksLikeSliderComponent(node);
	const box = getNodeBox(node);
	const trackBox = getNodeBox(parts.track);
	const strongShape = parts.orientation === "horizontal"
		? trackBox.width >= box.width * 0.45 && box.width >= box.height * 2
		: trackBox.height >= box.height * 0.45 && box.height >= box.width * 2;
	return nameHint || strongShape;
}

function findDirectSliderGroups(children: readonly SceneNode[]) {
	const matches: any[] = [];
	const used = new Set<string>();
	const candidates = children.filter((child) => child.visible && (child as any).type !== "TEXT");

	for (const first of candidates) {
		if (used.has(first.id)) continue;
		for (const second of candidates) {
			if (first === second || used.has(second.id)) continue;
			const parts = getSliderPartsFromCandidates([first, second]);
			if (!parts) continue;
			matches.push(parts);
			used.add(parts.track.id);
			used.add(parts.handle.id);
			break;
		}
	}

	return { matches, used };
}

function getAreaRatio(node: any, screenBox: { width: number; height: number }): number {
	const box = getNodeBox(node);
	const screenArea = Math.max(1, screenBox.width * screenBox.height);
	return (box.width * box.height) / screenArea;
}

function isSmallUiGroup(node: any, screenBox: { width: number; height: number }, maxAreaRatio: number, maxHeightRatio = 0.22): boolean {
	const box = getNodeBox(node);
	const heightRatio = box.height / Math.max(1, screenBox.height);
	return getAreaRatio(node, screenBox) <= maxAreaRatio && heightRatio <= maxHeightRatio;
}

function fitsControlBounds(
	node: any,
	screenBox: { width: number; height: number },
	normalAreaRatio: number,
	normalHeightRatio: number,
	explicitName: boolean,
	explicitAreaRatio = 0.65,
	explicitHeightRatio = 0.9
): boolean {
	return isSmallUiGroup(
		node,
		screenBox,
		explicitName ? explicitAreaRatio : normalAreaRatio,
		explicitName ? explicitHeightRatio : normalHeightRatio
	);
}

function hasOwnVisual(node: any): boolean {
	const fills = Array.isArray(node.fills) ? node.fills : [];
	if (fills.some((fill: any) => fill.visible !== false && fill.type !== "IMAGE")) return true;
	if (fills.some((fill: any) => fill.visible !== false && fill.type === "IMAGE")) return true;

	const strokes = Array.isArray(node.strokes) ? node.strokes : [];
	if (strokes.some((stroke: any) => stroke.visible !== false)) return true;

	const effects = Array.isArray(node.effects) ? node.effects : [];
	if (effects.some((effect: any) => effect.visible !== false)) return true;

	return false;
}

function removeTextDescendants(node: any) {
	if (!hasChildren(node)) return;

	for (const child of [...node.children] as any[]) {
		if (child.type === "TEXT") {
			child.remove();
		} else {
			removeTextDescendants(child);
		}
	}
}

function removeAllDescendants(node: any) {
	if (!hasChildren(node)) return;

	for (const child of [...node.children] as any[]) {
		child.remove();
	}
}

function shouldRemoveFromScreenBackground(node: SceneNode, screenBox: { width: number; height: number }): boolean {
	if (!node.visible) return true;
	if ((node as any).type === "TEXT") return true;
	if ((node as any).type === "ELLIPSE" && getImageFill(node)) return true;

	if (hasChildren(node)) {
		if (isLogoLikeGroup(node) && countTextDescendants(node) <= 2 && isSmallUiGroup(node, screenBox, 0.08, 0.18)) return true;
		if ((looksLikeSliderComponent(node) || looksLikeVisualSliderComponent(node)) && fitsControlBounds(node, screenBox, 0.18, 0.65, hasExplicitSliderName(node))) return true;
		if (looksLikeDropdownComponent(node) && fitsControlBounds(node, screenBox, 0.1, 0.18, hasExplicitDropdownName(node))) return true;
		if (looksLikeInputComponent(node) && fitsControlBounds(node, screenBox, 0.08, 0.22, hasExplicitInputName(node))) return true;
		if (looksLikeButtonComponent(node) && fitsControlBounds(node, screenBox, 0.05, 0.16, hasExplicitButtonName(node))) return true;
	}

	return false;
}

function removeSemanticDescendantsForScreenBackground(node: any, screenBox: { width: number; height: number }) {
	if (!hasChildren(node)) return;

	for (const child of [...node.children] as any[]) {
		if (shouldRemoveFromScreenBackground(child, screenBox)) {
			child.remove();
		} else {
			removeSemanticDescendantsForScreenBackground(child, screenBox);
		}
	}
}

function getTextPackageNode(node: any) {
	const fontName = node.fontName && typeof node.fontName === "object" ? node.fontName : {};
	const paint = getPaintInfo(node);
	const fill = paint.kind === "solid" ? paint.color : { r: 0, g: 0, b: 0, a: 1 };
	const box = getNodeBox(node);

	return {
		kind: "text",
		id: node.id,
		name: node.name,
		type: node.type,
		x: box.x,
		y: box.y,
		width: box.width,
		height: box.height,
		rotation: node.rotation || 0,
		text: node.characters || "",
		fontSize: typeof node.fontSize === "number" ? node.fontSize : 24,
		fontFamily: fontName.family || "",
		fontStyle: fontName.style || "",
		textAlignHorizontal: node.textAlignHorizontal || "LEFT",
		textAlignVertical: node.textAlignVertical || "TOP",
		lineHeight: node.lineHeight || null,
		fill,
		paint,
	};
}

function getControlTextInfo(node: SceneNode) {
	const textNode = getFirstTextDescendant(node);
	if (!textNode) return null;
	return getTextPackageNode(textNode);
}

async function exportAssetNode(
	node: any,
	assets: Record<string, string>,
	assetCache: Record<string, string>,
	index: number,
	kind?: string,
	boxOverride?: { x: number; y: number; width: number; height: number }
) {
	const box = boxOverride || getNodeBox(node);
	const renderBox = getRenderBox(node);
	const resolvedKind = kind || (node.type === "ELLIPSE" && getImageFill(node) ? "masked-image" : "image");
	const bytes = await node.exportAsync({
		format: "PNG",
		constraint: { type: "SCALE", value: 1 },
	});
	const assetKey = `${bytes.length}_${hashBytes(bytes)}`;
	let assetPath = assetCache[assetKey];
	if (!assetPath) {
		const assetName = `${String(index).padStart(3, "0")}_${sanitizeAssetName(resolvedKind)}_${sanitizeAssetName(node.name)}.png`;
		assetPath = `assets/${assetName}`;
		assetCache[assetKey] = assetPath;
		assets[assetPath] = bytesToBase64(bytes);
	}

	return {
		kind: resolvedKind,
		id: node.id,
		name: node.name,
		type: node.type,
		x: box.x,
		y: box.y,
		width: box.width,
		height: box.height,
		renderBounds: renderBox,
		rotation: node.rotation || 0,
		asset: assetPath,
		maskShape: node.type === "ELLIPSE" ? "ellipse" : "rect",
	};
}

async function exportScreenBackgroundNode(node: any, assets: Record<string, string>, assetCache: Record<string, string>, index: number, screenBox: { x: number; y: number; width: number; height: number }) {
	const clone = node.clone();
	try {
		removeAllDescendants(clone);
		clone.visible = true;
		return await exportAssetNode(clone, assets, assetCache, index, "screen-background", screenBox);
	} finally {
		clone.remove();
	}
}

async function exportContainerShellNode(node: any, assets: Record<string, string>, assetCache: Record<string, string>, index: number) {
	const box = getNodeBox(node);
	const clone = node.clone();
	try {
		removeAllDescendants(clone);
		clone.visible = true;
		return await exportAssetNode(clone, assets, assetCache, index, "panel-background", box);
	} finally {
		clone.remove();
	}
}

function getContainerPackageNode(node: any, children: any[]) {
	const box = getNodeBox(node);
	return {
		kind: "group",
		id: node.id,
		name: node.name,
		type: node.type,
		x: box.x,
		y: box.y,
		width: box.width,
		height: box.height,
		rotation: node.rotation || 0,
		children,
	};
}

async function exportControlNode(
	node: any,
	assets: Record<string, string>,
	assetCache: Record<string, string>,
	index: number,
	kind: "button" | "input-field" | "slider" | "dropdown",
	options: { bakeText?: boolean; alphaHitTest?: boolean; preserveTextLayout?: boolean; interactionOnly?: boolean } = {}
) {
	const box = getNodeBox(node);
	const clone = node.clone();
	try {
		if (!options.bakeText) removeTextDescendants(clone);
		clone.visible = true;
		const assetNode = await exportAssetNode(clone, assets, assetCache, index, kind, box);
		const textInfo = getControlTextInfo(node);
		return {
			...assetNode,
			label: options.bakeText ? "" : (textInfo?.text || ""),
			placeholder: options.bakeText ? "" : (textInfo?.text || ""),
			textBaked: !!options.bakeText,
			alphaHitTest: !!options.alphaHitTest || !!options.bakeText,
			preserveTextLayout: !!options.preserveTextLayout,
			interactionOnly: !!options.interactionOnly,
			textStyle: textInfo || null,
		};
	} finally {
		clone.remove();
	}
}

async function exportSliderNode(
	node: any,
	parts: any,
	assets: Record<string, string>,
	assetCache: Record<string, string>,
	index: number
) {
	const trackAsset = await exportAssetNode(parts.track, assets, assetCache, index, "slider-track");
	const handleAsset = await exportAssetNode(parts.handle, assets, assetCache, index, "slider-handle");
	const label = `${node.name || "slider"}`;

	return {
		kind: "slider",
		id: node.id || `${parts.track.id}_${parts.handle.id}`,
		name: label,
		type: node.type || "SLIDER",
		x: parts.box.x,
		y: parts.box.y,
		width: parts.box.width,
		height: parts.box.height,
		rotation: 0,
		asset: trackAsset.asset,
		orientation: parts.orientation,
		value: parts.value,
		track: {
			asset: trackAsset.asset,
			x: trackAsset.x,
			y: trackAsset.y,
			width: trackAsset.width,
			height: trackAsset.height,
			renderBounds: trackAsset.renderBounds,
		},
		handle: {
			asset: handleAsset.asset,
			x: handleAsset.x,
			y: handleAsset.y,
			width: handleAsset.width,
			height: handleAsset.height,
			renderBounds: handleAsset.renderBounds,
		},
	};
}

async function exportTextBackgroundNode(node: any, assets: Record<string, string>, assetCache: Record<string, string>, index: number) {
	const box = getNodeBox(node);
	const clone = node.clone();
	try {
		removeTextDescendants(clone);
		clone.visible = true;
		return await exportAssetNode(clone, assets, assetCache, index, "background", box);
	} finally {
		clone.remove();
	}
}

async function collectUnityPackageNodes(
	node: SceneNode,
	nodes: any[],
	assets: Record<string, string>,
	assetCache: Record<string, string>,
	exportedCount: { value: number },
	assetIndex: { value: number },
	screenBox: { width: number; height: number },
	isRoot = false
) {
	if (!node.visible || exportedCount.value >= unityPackageAssetLimitPerScreen) return;

	const box = getNodeBox(node);
	if (box.width <= 1 || box.height <= 1) return;

	if (node.type === "TEXT") {
		nodes.push(getTextPackageNode(node));
		return;
	}

	if (!isRoot && shouldBakeExplicitButton(node) && isSmallUiGroup(node, screenBox, 0.35, 0.85)) {
		exportedCount.value++;
		assetIndex.value++;
		nodes.push(await exportControlNode(node, assets, assetCache, assetIndex.value, "button", { alphaHitTest: true, preserveTextLayout: true, interactionOnly: true }));
		return;
	}

	if (getImageFill(node)) {
		exportedCount.value++;
		assetIndex.value++;
		nodes.push(await exportAssetNode(node, assets, assetCache, assetIndex.value));
		return;
	}

	if (hasChildren(node) && node.children.length > 0) {
		if (!isRoot && shouldBakeTextIntoVisual(node)) {
			exportedCount.value++;
			assetIndex.value++;
			if (looksLikeButtonComponent(node)) {
				nodes.push(await exportControlNode(node, assets, assetCache, assetIndex.value, "button", { bakeText: true }));
			} else {
				nodes.push(await exportAssetNode(node, assets, assetCache, assetIndex.value));
			}
			return;
		}

		if (!isRoot && isLogoLikeGroup(node) && countTextDescendants(node) <= 2 && isSmallUiGroup(node, screenBox, 0.08, 0.18)) {
			exportedCount.value++;
			assetIndex.value++;
			nodes.push(await exportAssetNode(node, assets, assetCache, assetIndex.value, "logo"));
			return;
		}

		if (!isRoot && (looksLikeSliderComponent(node) || looksLikeVisualSliderComponent(node)) && fitsControlBounds(node, screenBox, 0.18, 0.65, hasExplicitSliderName(node))) {
			exportedCount.value++;
			assetIndex.value++;
			const parts = getSliderParts(node);
			if (parts) nodes.push(await exportSliderNode(node, parts, assets, assetCache, assetIndex.value));
			else nodes.push(await exportControlNode(node, assets, assetCache, assetIndex.value, "slider"));
			return;
		}

		if (!isRoot && looksLikeDropdownComponent(node) && fitsControlBounds(node, screenBox, 0.1, 0.18, hasExplicitDropdownName(node))) {
			exportedCount.value++;
			assetIndex.value++;
			nodes.push(await exportControlNode(node, assets, assetCache, assetIndex.value, "dropdown"));
			return;
		}

		if (!isRoot && looksLikeInputComponent(node) && fitsControlBounds(node, screenBox, 0.08, 0.22, hasExplicitInputName(node))) {
			exportedCount.value++;
			assetIndex.value++;
			nodes.push(await exportControlNode(node, assets, assetCache, assetIndex.value, "input-field"));
			return;
		}

		if (!isRoot && looksLikeButtonComponent(node) && fitsControlBounds(node, screenBox, 0.05, 0.16, hasExplicitButtonName(node))) {
			exportedCount.value++;
			assetIndex.value++;
			nodes.push(await exportControlNode(node, assets, assetCache, assetIndex.value, "button"));
			return;
		}

		if (!isRoot && !hasTextDescendant(node) && !hasImageFillDescendant(node) && node.type !== "FRAME") {
			exportedCount.value++;
			assetIndex.value++;
			nodes.push(await exportAssetNode(node, assets, assetCache, assetIndex.value));
			return;
		}

		const childNodes: any[] = [];

		if (!isRoot && hasOwnVisual(node)) {
			exportedCount.value++;
			assetIndex.value++;
			childNodes.push(await exportContainerShellNode(node, assets, assetCache, assetIndex.value));
		}

		const directSliders = findDirectSliderGroups(node.children);
		for (const sliderParts of directSliders.matches) {
			exportedCount.value++;
			assetIndex.value++;
			childNodes.push(await exportSliderNode(
				{ id: `${sliderParts.track.id}_${sliderParts.handle.id}`, name: `${sliderParts.orientation} slider`, type: "SLIDER" },
				sliderParts,
				assets,
				assetCache,
				assetIndex.value
			));
			if (exportedCount.value >= unityPackageAssetLimitPerScreen) break;
		}

		for (const child of node.children) {
			if (directSliders.used.has(child.id)) continue;
			await collectUnityPackageNodes(child, childNodes, assets, assetCache, exportedCount, assetIndex, screenBox);
			if (exportedCount.value >= unityPackageAssetLimitPerScreen) break;
		}

		if (!isRoot && childNodes.length > 0) {
			nodes.push(getContainerPackageNode(node, childNodes));
		} else {
			nodes.push(...childNodes);
		}
		return;
	}

	if (!isRoot && hasOwnVisual(node)) {
		exportedCount.value++;
		assetIndex.value++;
		nodes.push(await exportAssetNode(node, assets, assetCache, assetIndex.value));
	}
}

async function createUnityPackage(selection: readonly SceneNode[]) {
	const roots = selection.filter((node: SceneNode) => node.visible);
	if (roots.length === 0) {
		throw new Error("Please select at least one visible node to export.");
	}

	const rootBox = unionBoxes(roots.map((node) => getNodeBox(node)));
	const assets: Record<string, string> = {};
	const assetCache: Record<string, string> = {};
	const assetIndex = { value: 0 };
	const screens: any[] = [];

	for (const root of roots) {
		const rootNode = root as any;
		const screenBox = getNodeBox(root);
		const screenNodes: any[] = [];
		const exportedCount = { value: 0 };
		exportedCount.value++;
		assetIndex.value++;
		screenNodes.push(await exportScreenBackgroundNode(root, assets, assetCache, assetIndex.value, screenBox));
		await collectUnityPackageNodes(root, screenNodes, assets, assetCache, exportedCount, assetIndex, screenBox, true);

		screens.push({
			id: root.id,
			name: rootNode.name || "Figma Screen",
			type: rootNode.type || "FRAME",
			...screenBox,
			fill: getFirstSolidFill(rootNode) || { r: 1, g: 1, b: 1, a: 1 },
			truncated: exportedCount.value >= unityPackageAssetLimitPerScreen,
			exportedAssetCount: exportedCount.value,
			exportLimit: unityPackageAssetLimitPerScreen,
			nodes: screenNodes,
		});
	}

	if (screens.every((screen) => screen.nodes.length === 0)) {
		throw new Error("No visible exportable nodes found. Select the main frame or group.");
	}

	const primaryRoot = roots[0] as any;
	return {
		format: "figma-unity-package",
		version: 2,
		name: roots.length === 1 ? (primaryRoot.name || "Figma Unity Export") : "Figma Screens",
		root: {
			name: roots.length === 1 ? (primaryRoot.name || "Figma Unity Export") : "Figma Screens",
			type: "DOCUMENT",
			...rootBox,
			fill: { r: 1, g: 1, b: 1, a: 1 },
		},
		screens,
		nodes: roots.length === 1 ? screens[0].nodes : [],
		assets,
	};
}

// TODO: keep list of fonts not found
async function getMatchingFont(fontStr: string, availableFonts: Font[]) {
	const familySplit = fontStr.split(/\s*,\s*/);

	for (const family of familySplit) {
		const normalized = normalizeName(family);
		for (const availableFont of availableFonts) {
			const normalizedAvailable = normalizeName(availableFont.fontName.family);
			if (normalizedAvailable === normalized) {
				const cached = fontCache[normalizedAvailable];
				if (cached) {
					return cached;
				}
				await figma.loadFontAsync(availableFont.fontName);
				fontCache[fontStr] = availableFont.fontName;
				fontCache[normalizedAvailable] = availableFont.fontName;
				return availableFont.fontName;
			}
		}
	}

	return defaultFont;
}

const fontCache: { [key: string]: FontName | undefined } = {};

// CSS variable naming utilities
function getCSSVariableNameWithCollection(variableName: string, collectionName?: string): string {
	// Normalize names for CSS variables
	const normalizeForCSS = (name: string) => 
		name.toLowerCase()
			.replace(/\s+/g, '-')
			.replace(/[^a-z0-9-]/g, '')
			.replace(/-+/g, '-')
			.replace(/^-|-$/g, '');

	const normalizedVar = normalizeForCSS(variableName);
	
	if (collectionName) {
		const normalizedCollection = normalizeForCSS(collectionName);
		return `--${normalizedCollection}-${normalizedVar}`;
	}
	
	return `--${normalizedVar}`;
}

// Enhanced CSS props extraction
async function getEnhancedCSSProps(element: any): Promise<any> {
	try {
		const cssProps = await element.getCSSAsync();
		
		// If the element has bound variables, enhance the CSS props with collection info
		if (element.boundVariables) {
			const enhancedProps = { ...cssProps };
			
			// Process bound variables to include collection names
			for (const [property, variable] of Object.entries(element.boundVariables)) {
				if (variable && typeof variable === 'object' && 'id' in variable && typeof property === 'string') {
					try {
						const figmaVariable = await figma.variables.getVariableByIdAsync(variable.id as string);
						if (figmaVariable) {
							const collection = await figma.variables.getVariableCollectionByIdAsync(figmaVariable.variableCollectionId);
							const collectionName = collection?.name;
							const variableName = figmaVariable.name;
							
							// Create enhanced CSS variable name
							const enhancedVarName = getCSSVariableNameWithCollection(variableName, collectionName);
							
							// Add to CSS props with collection info
							enhancedProps[`${property}-variable`] = enhancedVarName;
							enhancedProps[`${property}-collection`] = collectionName;
							enhancedProps[`${property}-variable-id`] = variable.id;
						}
					} catch (err) {
						console.warn(`Could not resolve variable for property ${property}:`, err);
					}
				}
			}
			
			return enhancedProps;
		}
		
		return cssProps;
	} catch (err) {
		console.warn("Could not get CSS properties:", err);
		return {};
	}
}

async function serialize(
	element: any,
	options: {
		withImages?: boolean;
		withCss?: boolean;
		withChildren?: boolean;
		// TODO
		withVectorsExported?: boolean;
	} = {}
): Promise<any> {
	let fills = (element.fills && (fastClone(element.fills) as Paint[])) || [];
	if (options.withImages && fills.length) {
		for (const fill of fills) {
			if (fill.type === "IMAGE" && fill.imageHash) {
				const image = figma.getImageByHash(fill.imageHash);
				try {
					const bytes = await image.getBytesAsync();
					(fill as any).intArr = bytes;
				} catch (err) {
					console.warn("Could not get image for layer", element, fill, err);
				}
			}
		}
	}

	// TODO: May have bg...
	const isSvg =
		(hasChildren(element) &&
			element.children.every((item) => item.type === "VECTOR")) ||
		element.type === "VECTOR";

	if (
		options.withImages &&
		// options.withVectorsExported !== false &&
		isSvg
	) {
		const image = await element.exportAsync({
			// TODO: use SVG for SVGs
			format: "PNG",
			constraint: {
				type: "SCALE",
				value: 2,
			},
		});
		fills = [
			{
				type: "IMAGE",
				visible: true,
				scaleMode: "FIT",
				...({ intArr: image } as any),
			},
		];
	}
	
	// Enhanced CSS props with collection info
	const cssProps = options.withCss === false ? {} : await getEnhancedCSSProps(element);

	// TODO: better way to enumerate everything, including getters, that is not function
	return {
		...cloneObject(element),
		cssProps,
		fills,
		type: element.type === "VECTOR" ? "RECTANGLE" : element.type,
		data: JSON.parse(element.getSharedPluginData("builder", "data") || "{}"),
		children:
			(options.withChildren &&
				element.children &&
				!isSvg &&
				(await Promise.all(
					element.children
						.filter((child: SceneNode) => child.visible)
						.map((child: any) => serialize(child as any, options))
				))) ||
			undefined,
	};
}

type AnyStringMap = { [key: string]: any };

function assign(a: BaseNode & AnyStringMap, b: AnyStringMap) {
	for (const key in b) {
		const value = b[key];
		if (key === "data" && value && typeof value === "object") {
			const currentData =
				JSON.parse(a.getSharedPluginData("builder", "data") || "{}") || {};
			const newData = value;
			const mergedData = Object.assign({}, currentData, newData);
			// TODO merge plugin data
			a.setSharedPluginData("builder", "data", JSON.stringify(mergedData));
		} else if (
			typeof value != "undefined" &&
			["width", "height", "type", "ref", "children", "svg"].indexOf(key) === -1
		) {
			try {
				a[key] = b[key];
			} catch (err) {
				console.warn(`Assign error for property "${key}"`, a, b, err);
			}
		}
	}
}

const isImportErrorsKey = "isImportErrors";

function clearAllErrors() {
	figma.currentPage.children.forEach((el) => {
		if (el.getPluginData(isImportErrorsKey) === "true") {
			el.remove();
		}
	});
}

const importableLayerTypes = new Set<NodeType>([
	"RECTANGLE",
	"FRAME",
	"TEXT",
	"COMPONENT",
	"LINE",
	"INSTANCE",
]);

const isNotImportable = (node: SceneNode) =>
	// Don't show warnings for invisble nodes, we don't import them
	!node.visible
		? false
		: ((node as FrameNode | GroupNode).children &&
			getLayout(node) === "unknown") ||
		!importableLayerTypes.has(node.type);

const getAbsolutePositionRelativeToArtboard = (node: SceneNode) => {
	if (
		typeof node.x !== "number" ||
		!node.parent ||
		["PAGE", "DOCUMENT"].includes(node.type)
	) {
		return { x: 0, y: 0 };
	}
	const position = {
		x: node.x,
		y: node.y,
	};

	if (["PAGE", "DOCUMENT"].includes(node.parent.type)) {
		return position;
	}

	let parent: SceneNode | null = node;
	while ((parent = parent.parent as SceneNode | null)) {
		if (!isGroupNode(parent) && typeof parent.x === "number") {
			position.x += parent.x;
			position.y += parent.y;
		}
		// This is the end
		if (["PAGE", "DOCUMENT"].includes(parent.parent!?.type)) {
			break;
		}
	}

	return position;
};

const getAbsolutePositionRelativeToRootLayer = (
	node: SceneNode,
	rootPosition: { x: number; y: number }
) => {
	const nodeAbsolutePosition = getAbsolutePositionRelativeToArtboard(node);
	return {
		x: nodeAbsolutePosition.x - rootPosition.x,
		y: nodeAbsolutePosition.y - rootPosition.y,
	};
};

const hasInvisibleParent = (node: SceneNode): boolean => {
	let parent: SceneNode | null = node;
	do {
		if (parent.visible === false) {
			return true;
		}
	} while ((parent = parent.parent as SceneNode | null));

	return false;
};

// Returns true if valid
async function checkIfCanGetCode() {
	clearAllErrors();
	const selected = figma.currentPage.selection[0];
	if (!selected) {
		return false;
	}

	const invalidLayers: SceneNode[] = [];

	await traverseLayers(selected, (node: SceneNode) => {
		if (!hasInvisibleParent(node) && isNotImportable(node)) {
			invalidLayers.push(node);
		}
	});

	if (invalidLayers.length) {
		const errorFrame = figma.createFrame();
		errorFrame.name = "Export to code errors - delete me anytime";
		const absolutePosition = getAbsolutePositionRelativeToArtboard(selected);
		errorFrame.x = absolutePosition.x;
		errorFrame.y = absolutePosition.y;
		errorFrame.fills = [];
		errorFrame.resize(selected.width || 1, selected.height || 1);
		errorFrame.setPluginData(isImportErrorsKey, "true");

		for (const invalidLayer of invalidLayers) {
			const errorLayer = figma.createRectangle();
			errorLayer.setPluginData(isImportErrorsKey, "true");
			if (invalidLayer.type === "VECTOR") {
				errorLayer.name = `"${invalidLayer.name}" needs to be a rasterized image`;
			} else {
				errorLayer.name = `"${invalidLayer.name}" needs to use autolayout`;
			}
			const { x, y } = getAbsolutePositionRelativeToRootLayer(
				invalidLayer,
				absolutePosition
			);
			errorLayer.x = x;
			errorLayer.y = y;
			errorLayer.fills = [];
			errorLayer.resize(invalidLayer.width || 1, invalidLayer.height || 1);

			errorLayer.strokeWeight = 4;
			errorLayer.strokes = [
				{
					type: "SOLID",
					visible: true,
					opacity: 1,
					blendMode: "NORMAL",
					color: {
						r: 1,
						g: 0,
						b: 0,
					},
				},
			];
			errorFrame.appendChild(errorLayer);
		}
	}

	return !invalidLayers.length;
}

// Calls to "parent.postMessage" from within the HTML page will trigger this
// callback. The callback will be passed the "pluginMessage" property of the
// posted message.
if (shouldShowUi) {
figma.ui.onmessage = async (msg) => {
	if (msg.type === "resize") {
		figma.ui.resize(msg.width, msg.height);
	}

	if (msg.type === "getStorage") {
		const data = await figma.clientStorage.getAsync("data");
		figma.ui.postMessage({
			type: "storage",
			data,
		});
	}
	if (msg.type === "init") {
		try {
			postSelection();
		} catch (error) {
			console.error("Failed to post initial selection:", error);
		}
	}
	if (msg.type === "setStorage") {
		const data = msg.data;
		figma.clientStorage.setAsync("data", data);
	}

	if (msg.type === "checkIfCanGetCode") {
		const canGet = await checkIfCanGetCode();
		try {
			figma.ui.postMessage({
				type: "canGetCode",
				value: canGet,
			});
		} catch (error) {
			console.error("Failed to send canGetCode result:", error);
		}
	}

	if (msg.type === "getSelectionWithImages") {
		try {
			figma.ui.postMessage({
				type: "selectionWithImages",
				elements: await Promise.all(
					figma.currentPage.selection.map((el) =>
						serialize(el as any, {
							withChildren: true,
							withImages: true,
						})
					)
				),
			});
		} catch (error) {
			console.error("Failed to send selection with images:", error);
		}
	}

	if (msg.type === "updateElements") {
		const elements = msg.elements;
		for (const element of elements) {
			const el = figma.getNodeById(element.id);
			if (el) {
				assign(el as any, element);
			}
		}
	}
	if (msg.type === "clearErrors") {
		clearAllErrors();
	}

	if (msg.type === "export-json") {
		try {
			figma.notify("Exporting selected node to JSON...");
			const selection = figma.currentPage.selection;
			if (selection.length === 0) {
				figma.ui.postMessage({
					type: "export-error",
					error: "Please select at least one node to export",
					errorData: { reason: "no_selection" },
					nodeData: null
				});
				figma.notify("Select at least one node before exporting.");
				return;
			}

			const objArr = fastClone(
				await Promise.all(
					selection.map((el) =>
						serializeWithErrorHandling(el as any, {
							withChildren: true,
							withCss: false,
						})
					)
				)
			);

			// Apply transformations
			objArr.forEach(obj => helpers.deletePropertiesRecursively(obj, [
				'relativeTransform',
				'isMask',
				'absoluteRenderBounds',
			]));

			const json = JSON.stringify(objArr, null, 2);
			if (json.length > 15 * 1024 * 1024) {
				throw new Error("Export is too large. Select a smaller frame/group or remove large embedded assets.");
			}
			
			figma.ui.postMessage({
				type: "export-done",
				json: json,
				suggestedFileName: getExportFileName(selection),
			});
			figma.notify("JSON is ready. Click Save JSON File in the plugin window.");

		} catch (err) {
			const error = err as Error;
			console.error("Export error:", error);
			figma.notify(`Export failed: ${error.message}`, { error: true });
			
			figma.ui.postMessage({
				type: "export-error",
				error: `Export failed: ${error.message}`,
				errorData: {
					message: error.message,
					stack: error.stack,
					timestamp: new Date().toISOString()
				},
				nodeData: figma.currentPage.selection.length > 0 ? 
					sanitizeNodeForLogging(figma.currentPage.selection[0]) : null
			});
		}
	}

	if (msg.type === "export-unity-package") {
		try {
			figma.notify("Exporting Unity package assets...");
			const selection = figma.currentPage.selection;
			if (selection.length === 0) {
				figma.ui.postMessage({
					type: "export-error",
					error: "Please select the main Figma frame/group before exporting a Unity package.",
					errorData: { reason: "no_selection" },
					nodeData: null
				});
				return;
			}

			const unityPackage = await createUnityPackage(selection);
			const json = JSON.stringify(unityPackage, null, 2);
			if (json.length > unityPackageSizeLimit) {
				throw new Error("Unity package is too large. Select a smaller frame/group or split the design into parts.");
			}

			figma.ui.postMessage({
				type: "export-done",
				kind: "unity-package",
				json,
				suggestedFileName: getExportFileName(selection),
			});
			figma.notify("Unity package is ready. Click Save JSON File in the plugin window.");
		} catch (err) {
			const error = err as Error;
			console.error("Unity package export error:", error);
			figma.notify(`Unity package export failed: ${error.message}`, { error: true });
			figma.ui.postMessage({
				type: "export-error",
				error: `Unity package export failed: ${error.message}`,
				errorData: {
					message: error.message,
					stack: error.stack,
					timestamp: new Date().toISOString()
				},
				nodeData: figma.currentPage.selection.length > 0 ?
					sanitizeNodeForLogging(figma.currentPage.selection[0]) : null
			});
		}
	}

	if (msg.type === "import") {
		const availableFonts = (await figma.listAvailableFontsAsync()).filter(
			(font) => font.fontName.style === "Regular"
		);
		await figma.loadFontAsync(defaultFont);
		const { data } = msg;
		const { layers } = data;
		const rects: SceneNode[] = [];
		let baseFrame: PageNode | FrameNode = figma.currentPage;
		// TS bug? TS is implying that frameRoot is PageNode and ignoring the type declaration
		// and the reassignment unless I force it to treat baseFrame as any
		let frameRoot: PageNode | FrameNode = baseFrame as any;
		for (const rootLayer of layers) {
			await traverseLayers(rootLayer, async (layer: any, parent) => {
				try {
					if (layer.type === "FRAME" || layer.type === "GROUP") {
						const frame = figma.createFrame();
						frame.x = layer.x;
						frame.y = layer.y;
						frame.resize(layer.width || 1, layer.height || 1);
						assign(frame, layer);
						rects.push(frame);
						((parent && (parent as any).ref) || baseFrame).appendChild(frame);
						layer.ref = frame;
						if (!parent) {
							frameRoot = frame;
							baseFrame = frame;
						}
						// baseFrame = frame;
					} else if (layer.type === "SVG") {
						const node = figma.createNodeFromSvg(layer.svg);
						node.x = layer.x;
						node.y = layer.y;
						node.resize(layer.width || 1, layer.height || 1);
						layer.ref = node;
						rects.push(node);
						assign(node, layer);
						((parent && (parent as any).ref) || baseFrame).appendChild(node);
					} else if (layer.type === "RECTANGLE") {
						const rect = figma.createRectangle();
						const imageFills = getImageFills(layer);
						if (imageFills) {
							await processImages(layer);
							if (imageFills.length && msg.blurImages) {
															(layer as RectangleNode).effects = [
								{
									type: "LAYER_BLUR",
									visible: true,
									radius: 13,
								} as any,
							];
								(layer as RectangleNode).name = "Example Image";
							}
						}
						assign(rect, layer);
						rect.resize(layer.width || 1, layer.height || 1);
						rects.push(rect);
						layer.ref = rect;
						((parent && (parent as any).ref) || baseFrame).appendChild(rect);
					} else if (layer.type == "TEXT") {
						const text = figma.createText();
						if (layer.fontFamily) {
							const cached = fontCache[layer.fontFamily];
							if (cached) {
								text.fontName = cached;
							} else {
								const family = await getMatchingFont(
									layer.fontFamily || "",
									availableFonts
								);
								text.fontName = family;
							}
							delete layer.fontFamily;
						}
						assign(text, layer);
						layer.ref = text;
						text.resize(layer.width || 1, layer.height || 1);
						text.textAutoResize = "HEIGHT";
						const lineHeight =
							(layer.lineHeight && layer.lineHeight.value) || layer.height;
						let adjustments = 0;
						while (
							typeof text.fontSize === "number" &&
							typeof layer.fontSize === "number" &&
							(text.height > Math.max(layer.height, lineHeight) * 1.2 ||
								text.width > layer.width * 1.2)
						) {
							// Don't allow changing more than ~30%
							if (adjustments++ > layer.fontSize * 0.3) {
								console.warn("Too many font adjustments", text, layer);
								// debugger
								break;
							}
							try {
								text.fontSize = text.fontSize - 1;
							} catch (err) {
								console.warn("Error on resize text:", layer, text, err);
							}
						}
						rects.push(text);
						((parent && (parent as any).ref) || baseFrame).appendChild(text);
					}
				} catch (err) {
					console.warn("Error on layer:", layer, err);
				}
			});
		}
		if (frameRoot.type === "FRAME") {
			figma.currentPage.selection = [frameRoot];
		}

		figma.ui.postMessage({
			type: "doneLoading",
			rootId: frameRoot.id,
		});

		figma.viewport.scrollAndZoomIntoView([frameRoot]);

		if (process.env.NODE_ENV !== "development") {
			figma.closePlugin();
		}
	}

	// Make sure to close the plugin when you're done. Otherwise the plugin will
	// keep running, which shows the cancel button at the bottom of the screen.
};
}

// Error handling utilities
function sanitizeNodeForLogging(node: any): any {
	try {
		const sanitized = {
			id: node.id,
			name: node.name,
			type: node.type,
			width: node.width,
			height: node.height,
			visible: node.visible,
			locked: node.locked,
			// Add properties that might be causing issues
			fills: node.fills ? JSON.parse(JSON.stringify(node.fills)) : undefined,
			strokes: node.strokes ? JSON.parse(JSON.stringify(node.strokes)) : undefined,
			effects: node.effects ? JSON.parse(JSON.stringify(node.effects)) : undefined,
			children: node.children ? node.children.map(child => ({
				id: child.id,
				name: child.name,
				type: child.type
			})) : undefined,
			// CSS props if available
			cssProps: null
		};

		// Try to get CSS props safely
		try {
			if (node.getCSSAsync) {
				// Note: We can't await here, but we can note it's available
				sanitized.cssProps = "Available (async)";
			}
		} catch (err) {
			const error = err as Error;
			sanitized.cssProps = `Error: ${error.message}`;
		}

		return sanitized;
	} catch (err) {
		const error = err as Error;
		return {
			id: node.id || "unknown",
			name: node.name || "unknown", 
			type: node.type || "unknown",
			error: `Failed to sanitize: ${error.message}`
		};
	}
}

async function serializeWithErrorHandling(element: any, options: any = {}): Promise<any> {
	try {
		return await serialize(element, options);
	} catch (err) {
		const error = err as Error;
		const nodeData = sanitizeNodeForLogging(element);
		const errorData = {
			message: error.message,
			stack: error.stack,
			timestamp: new Date().toISOString(),
			nodeId: element.id,
			nodeName: element.name,
			nodeType: element.type,
			options: options
		};

		console.error("Serialization error:", errorData, "Node data:", nodeData);
		
		// Send error to UI for user feedback (only if UI is available)
		try {
			if (figma.ui) {
				figma.ui.postMessage({
					type: "export-error",
					error: `Failed to process node "${element.name}" (${element.type}): ${error.message}`,
					errorData: errorData,
					nodeData: nodeData
				});
			}
		} catch (uiError) {
			// UI not available, silently continue (happens during codegen)
			console.log("UI not available for error reporting:", uiError);
		}

		throw err; // Re-throw to handle at higher level
	}
}

if (isCodegenMode) {
figma.codegen.on('generate', async ({ language, node }) => {
	try {
		console.log('DBG: Starting codegen for node:', node.name, node.type);

		let objArr = fastClone(
			await Promise.all(
				[node].map((el) =>
					serializeWithErrorHandling(el as any, {
						withChildren: true,
					})
				)
			)
		);

		// Apply transformations with error handling
		try {
			objArr.forEach(obj => helpers.deletePropertiesRecursively(obj, [
				'relativeTransform',
				'isMask',
				'absoluteRenderBounds',
			]));

			objArr = helpers.extractFieldsRecursively(objArr, ['cssProps', 'characters', 'type', 'name']);
		} catch (err) {
			const error = err as Error;
			console.error("Error during property processing:", error);
			const nodeData = sanitizeNodeForLogging(node);
			
			return [
				{
					language: "PLAINTEXT",
					code: JSON.stringify({
						error: "Failed to process node properties",
						details: error.message,
						nodeData: nodeData
					}, null, 2),
					title: "Export Error - Node Data",
				},
			];
		}

		const json = JSON.stringify(objArr, null, 2);
		console.log('DBG: Generate successful');

	return [
		{
			language: "PLAINTEXT",
			code: json,
				title: "Figma Node Export",
			},
		];

	} catch (err) {
		const error = err as Error;
		console.error('DBG: Generate failed:', error);
		
		// Create fallback data for debugging
		const nodeData = sanitizeNodeForLogging(node);
		const errorInfo = {
			error: error.message,
			stack: error.stack,
			timestamp: new Date().toISOString(),
			nodeData: nodeData,
			pluginVersion: "1.0.0",
			userAgent: typeof navigator !== 'undefined' ? navigator.userAgent : 'Unknown'
		};

		return [
			{
				language: "PLAINTEXT", 
				code: JSON.stringify(errorInfo, null, 2),
				title: "Export Error - Debug Info",
			},
		];
	}
});
}
