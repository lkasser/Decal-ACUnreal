#include "decal_view.h"

#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <map>
#include <optional>
#include <string>
#include <string_view>

#include "imgui_internal.h"  // the settings handler that keeps each window's theme and pin in the ini
#include "textures.h"

namespace overlay {
namespace {

// ---------------------------------------------------------------------------------------
// The themes.
//
// Everything here is read from Virindi View Service's own theme classes - Decal_Theme,
// Float_Theme and the four Minimalist ones, and the hot-dog stand VVS kept out of its lists -
// which set each value by name in their constructors; the names are kept beside the values
// so the two can be checked against each other. Images are the client's portal.dat ids, or
// VVS's embedded theme images, as keys the host resolves. A window is drawn in one theme,
// chosen from its title-bar icon's menu, as VVS let the player do.

// What the themes draw with the same art.
constexpr const char* kSliderNub = "portal:06001286";        // SliderNub, 7 by 12
constexpr const char* kComboArrow = "portal:060012B1";       // ComboArrowDown
constexpr const char* kBubbleTop = "portal:06004C60";        // VScrollBarBubbleTop
constexpr const char* kBubbleMiddle = "portal:06004C63";     // VScrollBarBubbleMiddle
constexpr const char* kBubbleMiddleHover = "portal:06004C64";
constexpr const char* kBubbleMiddleDrag = "portal:06004C65";
constexpr const char* kBubbleBottom = "portal:06004C66";     // VScrollBarBubbleBottom
constexpr const char* kAlphaUpUp = "portal:06004C7B";        // AlphaUpButtonUp
constexpr const char* kAlphaUpDown = "portal:06004C79";      // AlphaUpButtonDown
constexpr const char* kAlphaDownUp = "portal:06004C7E";      // AlphaDownButtonUp
constexpr const char* kAlphaDownDown = "portal:06004C7C";    // AlphaDownButtonDown
constexpr const char* kGhostUp = "vvs:Decal_Theme_Images.pin1.png";        // GhostButtonUp
constexpr const char* kGhostDown = "vvs:Decal_Theme_Images.pin2.png";      // GhostButtonDown
constexpr const char* kClickThroughOn = "vvs:Decal_Theme_Images.redarrow.png";      // CTButtonUp_On
constexpr const char* kClickThroughOnDown = "vvs:Decal_Theme_Images.redarrow_d.png";
constexpr const char* kClickThroughOff = "vvs:Decal_Theme_Images.whitearrow.png";   // CTButtonUp_Off
constexpr const char* kClickThroughOffDown = "vvs:Decal_Theme_Images.whitearrow_d.png";
constexpr const char* kTabActiveLeft = "vvs:Decal_Theme_Images.TabActiveLeft.png";
constexpr const char* kTabActiveCenter = "vvs:Decal_Theme_Images.TabActiveCenter.png";
constexpr const char* kTabActiveRight = "vvs:Decal_Theme_Images.TabActiveRight.png";
constexpr const char* kTabIdleLeft = "vvs:Decal_Theme_Images.TabInactiveLeft.png";
constexpr const char* kTabIdleCenter = "vvs:Decal_Theme_Images.TabInactiveCenter.png";
constexpr const char* kTabIdleRight = "vvs:Decal_Theme_Images.TabInactiveRight.png";
constexpr const char* kCloseUp = "portal:06005E64";          // CloseButtonUp, on black: Decal's and Minimalist's
constexpr const char* kCloseDown = "portal:06005E65";
constexpr const char* kFloatCloseUp = "portal:06001932";
constexpr const char* kFloatCloseDown = "portal:06001933";
constexpr const char* kStoneTile = "portal:0600128A";        // Decal's list, and Minimalist Green's every surface

// The Minimalist themes' own images, drawn as they are or redrawn as VVS redrew them - the
// glyph's black in one colour with an outline round it in another.
constexpr const char* kTick = "vvs:Minimalist_Theme_Images.tickmark.png";       // 13 by 13
constexpr const char* kArrowUp = "vvs:Minimalist_Theme_Images.arrowup.png";     // 12 by 12
constexpr const char* kArrowDown = "vvs:Minimalist_Theme_Images.arrowdown.png";
constexpr const char* kBlackX = "vvs:Minimalist_Black_Images.x.png";            // 10 by 10
constexpr const char* kBlackUp = "vvs:Minimalist_Black_Images.up.png";
constexpr const char* kBlackDown = "vvs:Minimalist_Black_Images.down.png";
constexpr const char* kSimpleX = "vvs:Icons_Simple.9x9_x.png";                  // 9 by 9: Minimalist Green's
constexpr const char* kSimplePlus = "vvs:Icons_Simple.9x9_plus.png";
constexpr const char* kSimpleMinus = "vvs:Icons_Simple.9x9_minus.png";
constexpr const char* kSimplePin = "vvs:Icons_Simple.9x9_p.png";
constexpr const char* kSimpleClick = "vvs:Icons_Simple.9x9_c.png";
constexpr const char* kGreenButton = "portal:06002344";      // Minimalist Green's buttons, drawn in nine pieces
constexpr const char* kGreenButtonDown = "portal:06002345";

// Decal's own bar arrows, from the ids its Inject.dll draws its switch-bar with: gold, like
// the window buttons, a yellow gem at rest and a red one pressed.
constexpr const char* kBarFold = "portal:06004C8A";          // pointing left
constexpr const char* kBarFoldPressed = "portal:06004C89";
constexpr const char* kBarUnfold = "portal:06004C8D";        // pointing right
constexpr const char* kBarUnfoldPressed = "portal:06004C8C";

// Decal's bar: the switch images, already cut to the switchbar's pill shape by the host.
constexpr const char* kSwitchOpen = "bar:open";
constexpr const char* kSwitchClosed = "bar:closed";
constexpr const char* kSwitchFaulted = "bar:faulted";

// Decal's bar in its compact form, as the standard client draws it: the switch bitmaps
// themselves, a square cut from the middle of each.
constexpr const char* kSwitchActiveTexture = "decal:Switch-Active.bmp";
constexpr const char* kSwitchInactiveTexture = "decal:Switch-Inactive.bmp";
constexpr const char* kSwitchDisabledTexture = "decal:Switchbar Disabled.bmp";

// Stand-ins for an image that has not arrived: close enough in colour that a window drawn
// before its art reads as the same window, not as a broken one.
constexpr ImU32 kParchmentStandIn = IM_COL32(176, 146, 96, 0xFF);
constexpr ImU32 kStoneStandIn = IM_COL32(52, 42, 30, 0xFF);

constexpr ImU32 kBlack = IM_COL32(0, 0, 0, 0xFF);
constexpr ImU32 kWhite = IM_COL32(0xFF, 0xFF, 0xFF, 0xFF);
constexpr ImU32 kClear = IM_COL32(0, 0, 0, 0);   // Color.Transparent, and "the theme sets nothing"

// VVS built most of what its Minimalist themes draw out of FillOption stacks: colours, images
// and bitmaps it drew for itself, each laid over a part of the rectangle being filled, one
// after another. A Layer is one of those.
enum class Piece {
    Colour,   // FillOption(Color)
    Image,    // an ACImage stretched over its part: the host's image, or redrawn as VVS redrew glyphs
    Tile,     // an image tiled from its part's corner, as ACImage's DrawTiled did
    Ramp,     // the 150-by-1 bitmap VVS drew from one colour to another and stretched: a gradient across
    Checker,  // the 12-by-600 bitmap VVS drew as a one-pixel checkerboard, tiled
    Nine,     // Minimalist Green's buttons: a portal image with five-pixel corners and the rest stretched
};

// Where in its rectangle a layer lies. None: a proportion of it, FillOption's RectangleF.
// Left, Top, Right and Bottom: a line a pixel wide along that edge, as SetPositionAbsolute put
// them. Inset: all but a pixel round the edge, as Minimalist Green's own fill drew.
enum class Edge { None, Left, Top, Right, Bottom, Inset };

struct Layer {
    Piece piece;
    ImU32 colour;       // Colour; Ramp's left end; Checker's even pixels; a redrawn Image's glyph
    ImU32 colour2;      // Ramp's right end; Checker's odd pixels; a redrawn Image's outline
    const char* image;  // Image, Tile, Nine
    float x, y, w, h;   // the part, in proportions of the rectangle
    Edge edge;
};

constexpr Layer Solid(ImU32 colour, float x = 0.0f, float y = 0.0f, float w = 1.0f, float h = 1.0f) {
    return {Piece::Colour, colour, kClear, nullptr, x, y, w, h, Edge::None};
}
constexpr Layer Line(ImU32 colour, Edge edge) { return {Piece::Colour, colour, kClear, nullptr, 0.0f, 0.0f, 1.0f, 1.0f, edge}; }
constexpr Layer Picture(const char* key) { return {Piece::Image, kClear, kClear, key, 0.0f, 0.0f, 1.0f, 1.0f, Edge::None}; }
constexpr Layer Glyph(const char* key, ImU32 glyph, ImU32 outline) {
    return {Piece::Image, glyph, outline, key, 0.0f, 0.0f, 1.0f, 1.0f, Edge::None};
}
constexpr Layer Tiled(const char* key, ImU32 stand_in) { return {Piece::Tile, stand_in, kClear, key, 0.0f, 0.0f, 1.0f, 1.0f, Edge::None}; }
constexpr Layer Ramp(ImU32 from, ImU32 to) { return {Piece::Ramp, from, to, nullptr, 0.0f, 0.0f, 1.0f, 1.0f, Edge::None}; }
constexpr Layer Checks(ImU32 even, ImU32 odd) { return {Piece::Checker, even, odd, nullptr, 0.0f, 0.0f, 1.0f, 1.0f, Edge::None}; }
constexpr Layer Nine(const char* key, ImU32 stand_in) { return {Piece::Nine, stand_in, kClear, key, 0.0f, 0.0f, 1.0f, 1.0f, Edge::None}; }
constexpr Layer Nothing() { return Solid(kClear); }
constexpr Layer At(Layer layer, float x, float y, float w, float h) {
    layer.x = x;
    layer.y = y;
    layer.w = w;
    layer.h = h;
    return layer;
}

// A surface: an image tiled across it - or stretched, where VVS filled it once rather than
// flooding it - or where the theme gives none, a colour; or a stack of layers. The colour is
// also the stand-in for a missing image, and what a stack is taken to be where only a colour
// will do.
struct Fill {
    const char* image;
    ImU32 colour;
    const Layer* layers = nullptr;
    size_t count = 0;
};

template <size_t N>
constexpr Fill Stack(const std::array<Layer, N>& layers, ImU32 colour) { return Fill{nullptr, colour, layers.data(), N}; }

template <size_t N>
constexpr Fill Stack(const Layer (&layers)[N], ImU32 colour) { return Fill{nullptr, colour, layers, N}; }

// A theme's frame around a window.
enum class FrameStyle { Solid, Float };

// How a theme draws a notebook's tabs: VVS's Decal tab images, a plain colour, or the stacks
// of the Minimalist themes.
enum class TabStyle { Images, Colour, Fills };

// A title-bar button's look: at rest, and held.
struct Face {
    Fill up;
    Fill down;
};

struct Theme {
    const char* name;

    Fill view;                  // ViewBackground
    Fill top;                   // ViewTopBackground, and ViewTopBackground_Selected unless top_focused
    float border;               // ViewBorder*_Size, for a view the player cannot resize
    float border_resizeable;    // ViewBorder*_Size_Resizeable
    FrameStyle frame;           // ViewBorder*, ViewCorner*
    ImU32 frame_colour;         // Solid: the frame's one colour
    float title_bar;            // ViewTitleBar_Size
    float title_rule;           // ViewTitleBottomBorder_Size
    Fill title_rule_fill;       // ViewTitleBottomBorder
    float icon_x, icon_y, icon_size;   // IconX, IconY, IconSize
    float title_x, title_y;            // TitleX, TitleY
    bool title_centred;                // TitleTextCenter, and DT_CENTER in TitleTextOptions
    float title_points;                // TitleFontSize
    bool title_bold;                   // TitleFontWeight 700
    ImU32 title_text, title_text_focused;   // TitleFontColor, TitleFontColor_Selected
    float button_size, buttons_left, buttons_down, button_spacing;  // ButtonSize, ButtonsLeft, ...
    bool alpha_buttons;                // ButtonAlphaShow
    Fill close_up;                     // CloseButtonUp
    Fill close_down;                   // CloseButtonDown

    ImU32 view_text;            // ViewText (checkbox labels use it too)
    ImU32 list_text;            // ListText
    ImU32 button_face, button_shadow, button_highlight, button_text;
    Fill text_box;              // TextBoxBackground: Decal's is an image in three pieces
    ImU32 text_box_text;        // TextBoxForeground
    Fill check_off;             // CheckBoxUnchecked
    Fill check_on;              // CheckBoxChecked
    Fill list;                  // ListBackground, ComboBackground_*
    Fill scroll_up;             // VScrollBarUpArrow
    Fill scroll_down;           // VScrollBarDownArrow
    Fill scroll_track;          // VScrollBarBackground
    ImU32 progress_filled, progress_empty;  // ProgressBarFilled, ProgressBarEmpty
    TabStyle tabs;              // TabView*
    ImU32 tab_colour;
    ImU32 tab_text_selected, tab_text_idle; // TabTextFontColor_*
    float tab_points;           // TabTextFontSize_*
    bool tab_bold;
    float tab_padding;          // TabTextHPadding
    float tab_cap;              // TabLeftBorder_Size
    float tab_cap_right;        // TabRightBorder_Size

    // What the Minimalist themes set beyond that. Each default is what the Decal and Float
    // themes were drawn with before there were others; an unset Fill, or a clear colour, is
    // "as those two did".
    Fill top_focused{};                          // ViewTopBackground_Selected
    ImU32 title_halo = kClear;                   // TitleFontShadowColor, where TitleFontShadowSize is 1
    ImU32 title_halo_focused = kClear;           // TitleFontShadowColor_Selected
    bool black_under = true;                     // see DrawDecalWindow
    float text_points = 8.0f;                    // DefaultTextFontSize
    ImU32 text_halo = kClear;                    // DefaultTextFontShadowColor, at DefaultTextFontShadowAlpha
    float text_box_points = 10.0f;               // TextBoxTextFontSize
    bool text_box_rule = false;                  // HudTextBox's ButtonHighlight and ButtonShadow edges
    Fill button_up{};                            // ButtonBackground; unset, button_face
    Fill button_down{};                          // ButtonBackground_Down
    Face alpha_up{{kAlphaUpUp, kClear}, {kAlphaUpDown, kClear}};          // AlphaUpButtonUp, _Down
    Face alpha_down{{kAlphaDownUp, kClear}, {kAlphaDownDown, kClear}};    // AlphaDownButtonUp, _Down
    Face ghost{{kGhostUp, kClear}, {kGhostDown, kClear}};                 // GhostButtonUp, _Down
    Face click_on{{kClickThroughOn, kClear}, {kClickThroughOnDown, kClear}};     // CTButtonUp_On, CTButtonDown_On
    Face click_off{{kClickThroughOff, kClear}, {kClickThroughOffDown, kClear}};  // CTButtonUp_Off, CTButtonDown_Off
    Fill slider_nub{kSliderNub, kClear};                  // SliderNub
    ImU32 slider_outer = IM_COL32(144, 120, 84, 0xFF);    // SliderBarOuter
    ImU32 slider_inner = IM_COL32(203, 167, 103, 0xFF);   // SliderBarInner
    Fill combo_box{};                            // ComboBackground_Unselected; unset, ListBackground
    Fill combo_list{};                           // ComboBackground_UnselectedDropdown; unset, ListBackground
    Fill combo_selected{nullptr, IM_COL32(0, 0, 0xFF, 0xFF)};   // ComboBackground_Selected
    Fill combo_selecting{nullptr, IM_COL32(0, 0, 100, 0xFF)};   // ComboBackground_Selecting
    Fill combo_arrow{kComboArrow, kClear};       // ComboArrowDown
    float scroll_button = 16.0f;                 // VScrollBarButtonSize: the bar's width, and each arrow's height
    float bubble_cap = 3.0f;                     // VScrollBarBarTBHeight
    Fill bubble_top{kBubbleTop, kClear};         // VScrollBarBubbleTop
    Fill bubble_middle{kBubbleMiddle, kClear};   // VScrollBarBubbleMiddle
    Fill bubble_hover{kBubbleMiddleHover, kClear};   // VScrollBarBubbleMiddleMouseover
    Fill bubble_drag{kBubbleMiddleDrag, kClear};     // VScrollBarBubbleMiddleDragging
    Fill bubble_bottom{kBubbleBottom, kClear};   // VScrollBarBubbleBottom
    float tab_height = 16.0f;                    // TabHeight
    ImU32 tab_halo_selected = kClear;            // TabTextFontShadowColor_Selected, where its size is 1
    ImU32 tab_halo_idle = kClear;                // TabTextFontShadowColor_Deselected
    Fill tab_selected[3] = {};                   // TabViewSelected_Left, _Center, _Right
    Fill tab_idle[3] = {};                       // TabViewNormal_Left, _Center, _Right
    ImU32 tooltip_border = kClear;               // TooltipBorder*; clear, Float's gold or the button shadow
    Fill menu_back{};                            // what VVS's menus showed through: set, its menus are drawn as VVS drew them
    ImU32 menu_text = kClear;                    // TooltipTextColor, which its menus were written in
    ImU32 menu_hover = kClear;                   // ComboBackground_Selected, under the item the pointer is on
    ImU32 menu_left = kClear;                    // MenuLeftAreaFill: the strip down a menu's left
    ImU32 bar_underlay = kClear;                 // Hint_VVSBarItemUnderlay; clear, ButtonShadow
};

// CloseButtonUp and CloseButtonDown, in Decal, Float and two of the Minimalists:
// FillOption(Color.Black, the image), the image on a square of black.
constexpr Layer kCloseLayers[] = {Solid(kBlack), Picture(kCloseUp)};
constexpr Layer kCloseHeldLayers[] = {Solid(kBlack), Picture(kCloseDown)};
constexpr Layer kFloatCloseLayers[] = {Solid(kBlack), Picture(kFloatCloseUp)};
constexpr Layer kFloatCloseHeldLayers[] = {Solid(kBlack), Picture(kFloatCloseDown)};

// VVS's Decal theme: parchment and gold, as Decal itself drew its views.
constexpr Theme kDecalTheme = {
    "Decal",
    {"portal:0600126F", kParchmentStandIn},
    {"portal:0600126F", kParchmentStandIn},
    0.0f, 4.0f,
    FrameStyle::Solid, IM_COL32(0x32, 0x29, 0x22, 0xFF),
    24.0f, 0.0f, {nullptr, IM_COL32(0x32, 0x29, 0x22, 0xFF)},
    4.0f, 4.0f, 16.0f,
    24.0f, 5.0f,
    false, 10.0f, true,
    IM_COL32(120, 120, 120, 0xFF), IM_COL32(0, 0, 0, 0xFF),
    16.0f, 0.0f, 0.0f, 0.0f,
    true,
    Stack(kCloseLayers, kBlack), Stack(kCloseHeldLayers, kBlack),

    IM_COL32(0, 0, 0, 0xFF),
    IM_COL32(0xFF, 0xFF, 0xFF, 0xFF),
    IM_COL32(0x8C, 0x50, 0x1E, 0xFF), IM_COL32(0x46, 0x28, 0x0F, 0xFF), IM_COL32(0xC5, 0xA7, 0x8E, 0xFF), IM_COL32(0xF0, 0xF0, 0x78, 0xFF),
    {"portal:06001276", IM_COL32(190, 160, 100, 0xFF)},
    IM_COL32(0, 0, 0, 0xFF),
    {"portal:0600191F", kClear}, {"portal:0600191A", kClear},
    {kStoneTile, kStoneStandIn},
    {"portal:060012B2", kClear}, {"portal:060012B1", kClear},
    {"portal:0600126E", kStoneStandIn},
    IM_COL32(0, 0, 0xFF, 0xFF), IM_COL32(0, 0, 0, 0xFF),
    TabStyle::Images, IM_COL32(0xB4, 0x7E, 0x2B, 0xFF),
    IM_COL32(0, 0, 0, 0xFF), IM_COL32(192, 192, 192, 0xFF),
    8.0f, false, 10.0f, 6.0f, 6.0f,
};

// VVS's Float theme: black glass edged in a thin gold rule, what the player's HUDs and most
// of their windows are drawn in.
constexpr Theme kFloatTheme = {
    "Float",
    {nullptr, IM_COL32(0, 0, 0, 180)},
    {nullptr, IM_COL32(0, 0, 0, 180)},
    5.0f, 5.0f,
    FrameStyle::Float, IM_COL32(0, 0, 0, 0xFF),
    16.0f, 3.0f, {"portal:0600612A", IM_COL32(144, 120, 84, 0xFF)},
    2.0f, 0.0f, 16.0f,
    24.0f, 0.0f,
    true, 8.0f, false,
    IM_COL32(120, 120, 120, 0xFF), IM_COL32(0xFF, 0xFF, 0xFF, 0xFF),
    14.0f, 1.0f, 1.0f, 2.0f,
    false,
    Stack(kFloatCloseLayers, kBlack), Stack(kFloatCloseHeldLayers, kBlack),

    IM_COL32(0xFF, 0xFF, 0xFF, 0xFF),
    IM_COL32(0xFF, 0xFF, 0xFF, 0xFF),
    IM_COL32(0, 0, 0, 220), IM_COL32(0x67, 0x55, 0x3B, 0xFF), IM_COL32(0x8F, 0x78, 0x54, 0xFF), IM_COL32(0xFF, 0xFF, 0xFF, 0xFF),
    {nullptr, IM_COL32(0, 0, 0, 220)},
    IM_COL32(0xFF, 0xFF, 0xFF, 0xFF),
    {"portal:06004D15", kClear}, {"portal:06004D17", kClear},
    {nullptr, IM_COL32(0, 0, 0, 220)},
    {"portal:06004C6C", kClear}, {"portal:06004C69", kClear},
    {nullptr, IM_COL32(0, 0, 0, 220)},
    IM_COL32(144, 120, 84, 220), IM_COL32(0, 0, 0, 220),
    TabStyle::Colour, IM_COL32(0, 0, 0, 180),
    IM_COL32(0xDA, 0xA5, 0x20, 0xFF), IM_COL32(0x80, 0x80, 0x80, 0xFF),
    7.0f, false, 6.0f, 0.0f, 0.0f,
};

// Float's frame: each edge a strip of its rule image tiled along it over black on the inner
// half, and a small gold square at each corner.
constexpr const char* kFloatRuleAcross = "portal:0600612A";  // 10 by 5: clear, gold, brown, gold, black
constexpr const char* kFloatRuleDown = "portal:0600612B";    // the same, standing up
constexpr const char* kFloatCorner = "portal:06006129";      // 5 by 5

// ---------------------------------------------------------------------------------------
// The Minimalist themes. VVS built the four alike, from the same FillOption stacks in each
// one's own colours, so the stacks are built here once and coloured for each.

// The proportions VVS laid those stacks out in, as the float arithmetic it did them in.
constexpr float kRow13 = 0.077f;         // a pixel row of a thirteen-pixel tab (num13)
constexpr float kTabLeftPart = 0.51f;    // of a tab's two-pixel left end (num14)
constexpr float kTabRightPart = 0.34f;   // of its three-pixel right end (num15)
constexpr float kPixel12 = 0.0834f;      // a pixel of a twelve-pixel scroll button (num16)
constexpr float kPixel16 = 0.0625f;      // of Minimalist Black's sixteen-pixel combo arrow
constexpr float kPixel10 = 0.1f;         // of Minimalist Black's ten-pixel title-bar buttons
constexpr float kOverHalf = 0.51f;       // the lit top and shaded bottom of the scroll bubble's ends (num17)

// A checkbox - drawn 13 pixels square - its face lit along the top and left and shaded along
// the right and bottom, and on a checked one the tick. Minimalist, Green and Transparent place
// the edges in proportion...
constexpr std::array<Layer, 6> Box(ImU32 face, ImU32 lit, ImU32 shade, Layer tick) {
    return {Solid(face), Solid(shade, 0.93f, 0.0f, 1.0f, 1.0f), Solid(shade, 0.0f, 0.93f, 1.0f, 1.0f),
            Solid(lit, 0.0f, 0.0f, 1.0f, 0.08f), Solid(lit, 0.0f, 0.0f, 0.08f, 1.0f), tick};
}

// ...and Minimalist Black and the hot-dog stand a pixel in from each edge.
constexpr std::array<Layer, 6> EdgedBox(ImU32 face, ImU32 lit, ImU32 shade, Layer tick) {
    return {Solid(face), Line(lit, Edge::Left), Line(lit, Edge::Top), Line(shade, Edge::Right), Line(shade, Edge::Bottom), tick};
}

// A scroll bar's arrow button, twelve pixels square - or Minimalist Black's combo arrow,
// sixteen: its face in one band or two, lit down the left and along the top, shaded down the
// right and along the bottom, and the arrow stretched over it all.
constexpr std::array<Layer, 7> ArrowButton(Layer face, Layer face2, ImU32 lit, ImU32 shade, Layer arrow, float pixel = kPixel12,
                                           float last = 11.0f * kPixel12) {
    return {face, face2, Solid(lit, 0.0f, 0.0f, pixel, 1.0f), Solid(lit, 0.0f, 0.0f, 1.0f, pixel),
            Solid(shade, last, 0.0f, pixel, 1.0f), Solid(shade, 0.0f, last, 1.0f, pixel), arrow};
}

// The scroll bar's bubble, in its three pieces: each the face, shaded down the right and lit
// down the left; the top end lit along its top, the bottom one shaded along its bottom.
constexpr std::array<Layer, 5> BubbleTop(Layer face, Layer face2, ImU32 lit, ImU32 shade) {
    return {face, face2, Solid(shade, 11.0f * kPixel12, 0.0f, kPixel12, 1.0f), Solid(lit, 0.0f, 0.0f, kPixel12, 1.0f),
            Solid(lit, 0.0f, 0.0f, 1.0f, kOverHalf)};
}
constexpr std::array<Layer, 4> BubbleMiddle(Layer face, Layer face2, ImU32 lit, ImU32 shade) {
    return {face, face2, Solid(shade, 11.0f * kPixel12, 0.0f, kPixel12, 1.0f), Solid(lit, 0.0f, 0.0f, kPixel12, 1.0f)};
}
constexpr std::array<Layer, 5> BubbleBottom(Layer face, Layer face2, ImU32 lit, ImU32 shade) {
    return {face, face2, Solid(shade, 0.0f, kOverHalf, 1.0f, kOverHalf), Solid(shade, 11.0f * kPixel12, 0.0f, kPixel12, 1.0f),
            Solid(lit, 0.0f, 0.0f, kPixel12, 1.0f)};
}

// A tab in three pieces - a two-pixel left end, the middle, a three-pixel right end - the
// chosen one rising a row above the rest with its corners cut, and the rest ruled along the
// bottom. VVS placed each face below the top rows; Minimalist Green passed its faces as a
// FillOption and a RectangleF, which FillOption's params constructor dropped, so there
// (`whole`) each face covers its piece.
constexpr Layer TabFace(Layer face, bool whole, float x, float y, float w, float h) {
    return whole ? At(face, 0.0f, 0.0f, 1.0f, 1.0f) : At(face, x, y, w, h);
}
constexpr std::array<Layer, 3> ChosenLeft(Layer face, ImU32 shade, bool whole) {
    return {Solid(shade, 0.0f, 3.0f * kRow13, kTabLeftPart, 1.0f), Solid(shade, kTabLeftPart, 2.0f * kRow13, kTabLeftPart, kRow13),
            TabFace(face, whole, kTabLeftPart, 3.0f * kRow13, kTabLeftPart, 1.0f)};
}
constexpr std::array<Layer, 2> ChosenMiddle(Layer face, ImU32 shade, bool whole) {
    return {TabFace(face, whole, 0.0f, 2.0f * kRow13, 1.0f, 1.0f), Solid(shade, 0.0f, kRow13, 1.0f, kRow13)};
}
constexpr std::array<Layer, 4> ChosenRight(Layer face, ImU32 shade, bool whole) {
    return {Solid(shade, 1.0f * kTabRightPart, 3.0f * kRow13, kTabRightPart, 1.0f), Solid(shade, 0.0f, 2.0f * kRow13, kTabRightPart, kRow13),
            TabFace(face, whole, 0.0f, 3.0f * kRow13, kTabRightPart, 1.0f),
            Solid(shade, 2.0f * kTabRightPart, 12.0f * kRow13, kTabRightPart, kRow13)};
}
constexpr std::array<Layer, 4> OtherLeft(Layer face, ImU32 shade, bool whole) {
    return {Solid(shade, 0.0f, 3.0f * kRow13, kTabLeftPart, 1.0f), Solid(shade, kTabLeftPart, 2.0f * kRow13, kTabLeftPart, kRow13),
            TabFace(face, whole, kTabLeftPart, 3.0f * kRow13, kTabLeftPart, 1.0f), Solid(shade, 0.0f, 12.0f * kRow13, 1.0f, kRow13)};
}
constexpr std::array<Layer, 3> OtherMiddle(Layer face, ImU32 shade, bool whole) {
    return {TabFace(face, whole, 0.0f, 2.0f * kRow13, 1.0f, 1.0f), Solid(shade, 0.0f, kRow13, 1.0f, kRow13),
            Solid(shade, 0.0f, 12.0f * kRow13, 1.0f, kRow13)};
}
constexpr std::array<Layer, 4> OtherRight(Layer face, ImU32 shade, bool whole) {
    return {Solid(shade, kTabRightPart, 3.0f * kRow13, kTabRightPart, 1.0f), Solid(shade, 0.0f, 2.0f * kRow13, kTabRightPart, kRow13),
            TabFace(face, whole, 0.0f, 3.0f * kRow13, kTabRightPart, 1.0f), Solid(shade, 0.0f, 12.0f * kRow13, 1.0f, kRow13)};
}

// Minimalist Black's title-bar buttons, and the hot-dog stand's: the glyph, and a pixel's
// frame round the button.
constexpr std::array<Layer, 5> FramedGlyph(Layer glyph, ImU32 lit, ImU32 shade) {
    return {glyph, Solid(lit, 0.0f, 0.0f, kPixel10, 1.0f), Solid(lit, 0.0f, 0.0f, 1.0f, kPixel10),
            Solid(shade, 9.0f * kPixel10, 0.0f, kPixel10, 1.0f), Solid(shade, 0.0f, 9.0f * kPixel10, 1.0f, kPixel10)};
}

// Their slider nub, 7 by 12: a face, and a pixel's edge all round.
constexpr std::array<Layer, 5> Nub(ImU32 face, ImU32 edge) {
    return {Solid(face), Solid(edge, 0.0f, 0.0f, 1.0f, 1.0f / 12.0f), Solid(edge, 0.0f, 0.0f, 1.0f / 7.0f, 1.0f),
            Solid(edge, 1.0f - 1.0f / 7.0f, 0.0f, 1.0f / 7.0f, 1.0f), Solid(edge, 0.0f, 1.0f - 1.0f / 12.0f, 1.0f, 1.0f / 12.0f)};
}

// Minimalist Green's own fill for its text and combo boxes: the olive, ruled round in tan.
constexpr std::array<Layer, 5> RuledBox(ImU32 face, ImU32 rule) {
    return {Solid(face), Line(rule, Edge::Left), Line(rule, Edge::Top), Line(rule, Edge::Right), Line(rule, Edge::Bottom)};
}

// The last pixel of VVS's 150-pixel title-bar gradients, which stepped from the first colour
// towards the second by a 150th a pixel and so stopped short of it.
constexpr ImU32 kMinTitleFrom = IM_COL32(0x80, 0x80, 0x80, 0xFF);   // color7
constexpr ImU32 kMinTitleTo = IM_COL32(0xBF, 0xBF, 0xBF, 0xFF);     // towards color8, C0C0C0
constexpr ImU32 kMinFocusedFrom = IM_COL32(0x0A, 0x24, 0x6A, 0xFF); // color5
constexpr ImU32 kMinFocusedTo = IM_COL32(0xA4, 0xC8, 0xEF, 0xFF);   // towards color6, A6CAF0

// VVS's Minimalist theme: the grey, white and blue of a Windows 2000 dialog.
constexpr ImU32 kMinFace = IM_COL32(0xD4, 0xD0, 0xC8, 0xFF);      // color
constexpr ImU32 kMinShade = IM_COL32(0x80, 0x80, 0x80, 0xFF);     // color3
constexpr ImU32 kMinChosen = IM_COL32(0xA6, 0xCA, 0xF0, 0xFF);    // color9
constexpr std::array<Layer, 1> kMinTitle = {Ramp(kMinTitleFrom, kMinTitleTo)};
constexpr std::array<Layer, 1> kMinTitleFocused = {Ramp(kMinFocusedFrom, kMinFocusedTo)};
constexpr auto kMinCheckOff = Box(kWhite, kWhite, kMinShade, Nothing());
constexpr auto kMinCheckOn = Box(kWhite, kWhite, kMinShade, Picture(kTick));
constexpr auto kMinScrollUp = ArrowButton(Solid(kMinFace), Nothing(), kWhite, kMinShade, Picture(kArrowUp));
constexpr auto kMinScrollDown = ArrowButton(Solid(kMinFace), Nothing(), kWhite, kMinShade, Picture(kArrowDown));
constexpr std::array<Layer, 1> kMinTrack = {Checks(kWhite, kMinFace)};
constexpr auto kMinBubbleTop = BubbleTop(Solid(kMinFace), Nothing(), kWhite, kMinShade);
constexpr auto kMinBubbleMiddle = BubbleMiddle(Solid(kMinFace), Nothing(), kWhite, kMinShade);
constexpr auto kMinBubbleBottom = BubbleBottom(Solid(kMinFace), Nothing(), kWhite, kMinShade);
constexpr auto kMinTabLeft = ChosenLeft(Solid(kWhite), kMinShade, false);
constexpr auto kMinTabMiddle = ChosenMiddle(Solid(kWhite), kMinShade, false);
constexpr auto kMinTabRight = ChosenRight(Solid(kWhite), kMinShade, false);
constexpr auto kMinIdleLeft = OtherLeft(Solid(kMinFace), kMinShade, false);
constexpr auto kMinIdleMiddle = OtherMiddle(Solid(kMinFace), kMinShade, false);
constexpr auto kMinIdleRight = OtherRight(Solid(kMinFace), kMinShade, false);

constexpr Theme kMinimalistTheme = {
    .name = "Minimalist",
    .view = {nullptr, kMinFace},
    .top = Stack(kMinTitle, kMinTitleFrom),
    .border = 1.0f,
    .border_resizeable = 3.0f,
    .frame = FrameStyle::Solid,
    .frame_colour = kBlack,                   // ViewBorder*, ViewCorner*
    .title_bar = 12.0f,
    .title_rule = 0.0f,
    .title_rule_fill = {nullptr, kBlack},
    .icon_x = 1.0f, .icon_y = 1.0f, .icon_size = 10.0f,
    .title_x = 13.0f, .title_y = -2.0f,
    .title_centred = false,
    .title_points = 8.0f,
    .title_bold = false,                      // TitleFontWeight 400
    .title_text = kWhite, .title_text_focused = kWhite,
    .button_size = 10.0f, .buttons_left = 1.0f, .buttons_down = 1.0f, .button_spacing = 1.0f,
    .alpha_buttons = true,
    .close_up = Stack(kCloseLayers, kBlack),
    .close_down = Stack(kCloseHeldLayers, kBlack),
    .view_text = kBlack,
    .list_text = kBlack,
    .button_face = kMinFace, .button_shadow = kMinShade, .button_highlight = kWhite, .button_text = kBlack,
    .text_box = {nullptr, kWhite},
    .text_box_text = kBlack,
    .check_off = Stack(kMinCheckOff, kWhite),
    .check_on = Stack(kMinCheckOn, kWhite),
    .list = {nullptr, kWhite},
    .scroll_up = Stack(kMinScrollUp, kMinFace),
    .scroll_down = Stack(kMinScrollDown, kMinFace),
    .scroll_track = Stack(kMinTrack, kMinFace),
    .progress_filled = IM_COL32(0, 0, 0xFF, 0xFF), .progress_empty = kBlack,
    .tabs = TabStyle::Fills,
    .tab_colour = kMinFace,
    .tab_text_selected = kBlack, .tab_text_idle = kBlack,
    .tab_points = 7.0f,
    .tab_bold = false,
    .tab_padding = 10.0f,
    .tab_cap = 2.0f,
    .tab_cap_right = 3.0f,
    .top_focused = Stack(kMinTitleFocused, kMinFocusedFrom),
    .text_box_rule = true,
    .button_up = {nullptr, kMinFace},
    .button_down = {nullptr, kMinFace},
    .combo_selected = {nullptr, kMinChosen},
    .combo_selecting = {nullptr, kMinChosen},
    .scroll_button = 12.0f,
    .bubble_cap = 2.0f,
    .bubble_top = Stack(kMinBubbleTop, kMinFace),
    .bubble_middle = Stack(kMinBubbleMiddle, kMinFace),
    .bubble_hover = Stack(kMinBubbleMiddle, kMinFace),
    .bubble_drag = Stack(kMinBubbleMiddle, kMinFace),
    .bubble_bottom = Stack(kMinBubbleBottom, kMinFace),
    .tab_height = 13.0f,
    .tab_selected = {Stack(kMinTabLeft, kWhite), Stack(kMinTabMiddle, kWhite), Stack(kMinTabRight, kWhite)},
    .tab_idle = {Stack(kMinIdleLeft, kMinFace), Stack(kMinIdleMiddle, kMinFace), Stack(kMinIdleRight, kMinFace)},
    .tooltip_border = kMinShade,              // TooltipBorder*: ButtonShadow
    .menu_back = {nullptr, kMinFace},         // ViewBackground
    .menu_text = kBlack,
    .menu_hover = kMinChosen,
    .menu_left = kMinShade,                   // color7
    .bar_underlay = kMinShade,                // ButtonShadow
};

// VVS's Minimalist Green: olive and tan over the stone of Decal's lists, in Verdana.
constexpr ImU32 kGreenOlive = IM_COL32(0x49, 0x5F, 0x13, 0xFF);   // m_a
constexpr ImU32 kGreenTan = IM_COL32(0xD9, 0xBB, 0x71, 0xFF);     // m_b
constexpr ImU32 kGreenGrey = IM_COL32(0x80, 0x80, 0x80, 0xFF);    // m_d
constexpr ImU32 kGreenBrown = IM_COL32(0x45, 0x33, 0x07, 0xFF);   // e
constexpr ImU32 kGreenDark = IM_COL32(0x28, 0x34, 0x0A, 0xFF);    // i
constexpr ImU32 kRed = IM_COL32(0xFF, 0, 0, 0xFF);                // Color.Red
constexpr Layer kGreenStone = Tiled(kStoneTile, kStoneStandIn);   // fillOption: ACImage(100668042, DrawTiled)
constexpr auto kGreenCheckOff = Box(kGreenOlive, kGreenBrown, kGreenGrey, Nothing());
constexpr auto kGreenCheckOn = Box(kGreenOlive, kGreenBrown, kGreenGrey, Glyph(kTick, kWhite, kGreenBrown));
constexpr auto kGreenScrollUp = ArrowButton(Solid(kGreenOlive), Nothing(), kGreenBrown, kGreenGrey, Glyph(kArrowUp, kWhite, kGreenBrown));
constexpr auto kGreenScrollDown = ArrowButton(Solid(kGreenOlive), Nothing(), kGreenBrown, kGreenGrey, Glyph(kArrowDown, kWhite, kGreenBrown));
constexpr auto kGreenBubbleTop = BubbleTop(Solid(kGreenOlive), Nothing(), kGreenBrown, kGreenGrey);
constexpr auto kGreenBubbleMiddle = BubbleMiddle(Solid(kGreenOlive), Nothing(), kGreenBrown, kGreenGrey);
constexpr auto kGreenBubbleBottom = BubbleBottom(Solid(kGreenOlive), Nothing(), kGreenBrown, kGreenGrey);
constexpr auto kGreenTabLeft = ChosenLeft(Solid(kGreenOlive), kGreenGrey, true);
constexpr auto kGreenTabMiddle = ChosenMiddle(Solid(kGreenOlive), kGreenGrey, true);
constexpr auto kGreenTabRight = ChosenRight(Solid(kGreenOlive), kGreenGrey, true);
constexpr auto kGreenIdleLeft = OtherLeft(kGreenStone, kGreenGrey, true);
constexpr auto kGreenIdleMiddle = OtherMiddle(kGreenStone, kGreenGrey, true);
constexpr auto kGreenIdleRight = OtherRight(kGreenStone, kGreenGrey, true);
constexpr auto kGreenBox = RuledBox(kGreenOlive, kGreenTan);        // its method b
constexpr std::array<Layer, 1> kGreenChosen = {Line(kGreenDark, Edge::Inset)};   // its method a
constexpr std::array<Layer, 1> kGreenFace = {Nine(kGreenButton, kGreenOlive)};    // its method d
constexpr std::array<Layer, 1> kGreenFaceDown = {Nine(kGreenButtonDown, kGreenOlive)};  // and c
constexpr std::array<Layer, 1> kGreenClose = {Glyph(kSimpleX, kWhite, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenCloseHeld = {Glyph(kSimpleX, kRed, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenPlus = {Glyph(kSimplePlus, kWhite, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenPlusHeld = {Glyph(kSimplePlus, kRed, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenMinus = {Glyph(kSimpleMinus, kWhite, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenMinusHeld = {Glyph(kSimpleMinus, kRed, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenPin = {Glyph(kSimplePin, kWhite, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenPinHeld = {Glyph(kSimplePin, kRed, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenClickOn = {Glyph(kSimpleClick, kRed, kGreenBrown)};
constexpr std::array<Layer, 1> kGreenClickOff = {Glyph(kSimpleClick, kWhite, kGreenBrown)};

constexpr Theme kMinimalistGreenTheme = {
    .name = "Minimalist Green",
    .view = {kStoneTile, kStoneStandIn},
    .top = {nullptr, kGreenBrown},
    .border = 1.0f,
    .border_resizeable = 3.0f,
    .frame = FrameStyle::Solid,
    .frame_colour = kGreenTan,
    .title_bar = 11.0f,
    .title_rule = 0.0f,
    .title_rule_fill = {nullptr, kBlack},
    .icon_x = 1.0f, .icon_y = 1.0f, .icon_size = 9.0f,
    .title_x = 13.0f, .title_y = -2.0f,
    .title_centred = false,
    .title_points = 7.0f,
    .title_bold = false,                      // TitleFontWeight k, 400
    .title_text = kWhite, .title_text_focused = kWhite,
    .button_size = 9.0f, .buttons_left = 1.0f, .buttons_down = 1.0f, .button_spacing = 1.0f,
    .alpha_buttons = true,
    .close_up = Stack(kGreenClose, kGreenOlive),
    .close_down = Stack(kGreenCloseHeld, kGreenOlive),
    .view_text = kWhite,                      // f
    .list_text = kWhite,                      // g
    .button_face = kGreenOlive, .button_shadow = kClear, .button_highlight = kClear, .button_text = kWhite,  // an empty FillOption()
    .text_box = Stack(kGreenBox, kGreenOlive),
    .text_box_text = kWhite,
    .check_off = Stack(kGreenCheckOff, kGreenOlive),
    .check_on = Stack(kGreenCheckOn, kGreenOlive),
    .list = {kStoneTile, kStoneStandIn},
    .scroll_up = Stack(kGreenScrollUp, kGreenOlive),
    .scroll_down = Stack(kGreenScrollDown, kGreenOlive),
    .scroll_track = {nullptr, kBlack},
    .progress_filled = kGreenOlive, .progress_empty = kBlack,
    .tabs = TabStyle::Fills,
    .tab_colour = kGreenOlive,
    .tab_text_selected = kWhite, .tab_text_idle = kWhite,
    .tab_points = 7.0f,
    .tab_bold = false,
    .tab_padding = 10.0f,
    .tab_cap = 2.0f,
    .tab_cap_right = 3.0f,
    .top_focused = {nullptr, kGreenOlive},
    .title_halo = kClear,                     // TitleFontShadowSize 0
    .title_halo_focused = kGreenBrown,
    .text_points = 7.0f,
    .text_halo = kGreenBrown,
    .text_box_points = 9.0f,
    .text_box_rule = true,
    .button_up = Stack(kGreenFace, kGreenOlive),
    .button_down = Stack(kGreenFaceDown, kGreenOlive),
    .alpha_up = {Stack(kGreenPlus, kGreenOlive), Stack(kGreenPlusHeld, kGreenOlive)},
    .alpha_down = {Stack(kGreenMinus, kGreenOlive), Stack(kGreenMinusHeld, kGreenOlive)},
    .ghost = {Stack(kGreenPin, kGreenOlive), Stack(kGreenPinHeld, kGreenOlive)},
    .click_on = {Stack(kGreenClickOn, kGreenOlive), Stack(kGreenClickOn, kGreenOlive)},
    .click_off = {Stack(kGreenClickOff, kGreenOlive), Stack(kGreenClickOff, kGreenOlive)},
    .combo_box = Stack(kGreenBox, kGreenOlive),
    .combo_list = Stack(kGreenBox, kGreenOlive),
    .combo_selected = Stack(kGreenChosen, kGreenDark),
    .combo_selecting = Stack(kGreenChosen, kGreenDark),
    .scroll_button = 12.0f,
    .bubble_cap = 2.0f,
    .bubble_top = Stack(kGreenBubbleTop, kGreenOlive),
    .bubble_middle = Stack(kGreenBubbleMiddle, kGreenOlive),
    .bubble_hover = Stack(kGreenBubbleMiddle, kGreenOlive),
    .bubble_drag = Stack(kGreenBubbleMiddle, kGreenOlive),
    .bubble_bottom = Stack(kGreenBubbleBottom, kGreenOlive),
    .tab_height = 13.0f,
    .tab_halo_selected = kGreenBrown,
    .tab_halo_idle = kGreenBrown,
    .tab_selected = {Stack(kGreenTabLeft, kGreenOlive), Stack(kGreenTabMiddle, kGreenOlive), Stack(kGreenTabRight, kGreenOlive)},
    .tab_idle = {Stack(kGreenIdleLeft, kStoneStandIn), Stack(kGreenIdleMiddle, kStoneStandIn), Stack(kGreenIdleRight, kStoneStandIn)},
    .tooltip_border = kGreenTan,              // TooltipBorder*: ViewBorderTop
    .menu_back = {kStoneTile, kStoneStandIn},
    .menu_text = kWhite,
    .menu_hover = kGreenDark,
    .menu_left = kGreenBrown,                 // e
    .bar_underlay = kGreenOlive,              // m_a
};

// VVS's Minimalist Black: black and charcoal, lettered in teal.
constexpr ImU32 kBlackPanel = IM_COL32(0x1C, 0x1C, 0x1C, 0xFF);   // color2
constexpr ImU32 kBlackEdge = IM_COL32(0x36, 0x36, 0x36, 0xFF);    // color3, and value
constexpr ImU32 kBlackBand = IM_COL32(0x2B, 0x2B, 0x2B, 0xFF);    // -13948117
constexpr ImU32 kBlackNavy = IM_COL32(0x00, 0x2A, 0x2F, 0xFF);    // color4: its glyphs' outline and its shadows
constexpr ImU32 kBlackTeal = IM_COL32(0x4D, 0xB5, 0xC2, 0xFF);    // color5: its glyphs and its lettering
constexpr ImU32 kBlackPale = IM_COL32(0xEF, 0xF0, 0xF2, 0xFF);    // -1052430
constexpr ImU32 kBlackChosen = IM_COL32(0x03, 0x33, 0x37, 0xFF);  // -16567497
constexpr std::array<Layer, 2> kBlackTitle = {Solid(IM_COL32(0x32, 0x32, 0x32, 0xFF), 0.0f, 0.0f, 1.0f, 0.5f),
                                              Solid(kBlack, 0.0f, 0.5f, 1.0f, 0.5f)};
constexpr std::array<Layer, 3> kBlackFace = {Solid(kBlackBand, 0.0f, 0.0f, 1.0f, 0.47f), Solid(IM_COL32(0x20, 0x20, 0x1E, 0xFF), 0.0f, 0.47f, 1.0f, 0.06f),
                                             Solid(IM_COL32(0x19, 0x13, 0x15, 0xFF), 0.0f, 0.47f + 0.06f, 1.0f, 0.47f)};
constexpr std::array<Layer, 3> kBlackFaceDown = {Solid(IM_COL32(0x19, 0x13, 0x15, 0xFF), 0.0f, 0.0f, 1.0f, 0.47f),
                                                 Solid(IM_COL32(0x20, 0x20, 0x1E, 0xFF), 0.0f, 0.47f, 1.0f, 0.06f),
                                                 Solid(kBlackBand, 0.0f, 0.47f + 0.06f, 1.0f, 0.47f)};
constexpr Layer kBlackDarkOver = Solid(kBlack, 0.0f, 0.0f, 1.0f, 0.7f);        // fillOption6
constexpr Layer kBlackLightUnder = Solid(kBlackBand, 0.0f, 0.7f, 1.0f, 0.3f);
constexpr Layer kBlackLightOver = Solid(kBlackBand, 0.0f, 0.0f, 1.0f, 0.3f);   // fillOption5
constexpr Layer kBlackDarkUnder = Solid(kBlack, 0.0f, 0.3f, 1.0f, 0.7f);
constexpr Layer kBlackLeftHalf = Solid(kBlack, 0.0f, 0.0f, 0.5f, 1.0f);
constexpr Layer kBlackRightHalf = Solid(kBlackBand, 0.5f, 0.0f, 0.5f, 1.0f);
constexpr auto kBlackCheckOff = EdgedBox(kBlackPanel, kBlackEdge, kBlackEdge, Nothing());
constexpr auto kBlackCheckOn = EdgedBox(kBlackPanel, kBlackEdge, kBlackEdge, Glyph(kTick, kBlackTeal, kBlackNavy));
constexpr auto kBlackScrollUp = ArrowButton(kBlackDarkOver, kBlackLightUnder, kBlackEdge, kBlackEdge, Glyph(kArrowUp, kBlackTeal, kBlackNavy));
constexpr auto kBlackScrollDown =
    ArrowButton(kBlackLightOver, kBlackDarkUnder, kBlackEdge, kBlackEdge, Glyph(kArrowDown, kBlackTeal, kBlackNavy));
constexpr auto kBlackComboArrow = ArrowButton(kBlackLightOver, kBlackDarkUnder, kBlackEdge, kBlackEdge,
                                              Glyph(kArrowDown, kBlackTeal, kBlackNavy), kPixel16, 15.0f * kPixel16);
constexpr auto kBlackBubbleTop = BubbleTop(kBlackLeftHalf, kBlackRightHalf, kBlackEdge, kBlackEdge);
constexpr auto kBlackBubbleMiddle = BubbleMiddle(kBlackLeftHalf, kBlackRightHalf, kBlackEdge, kBlackEdge);
constexpr auto kBlackBubbleBottom = BubbleBottom(kBlackLeftHalf, kBlackRightHalf, kBlackEdge, kBlackEdge);
constexpr auto kBlackNub = Nub(IM_COL32(0x70, 0x70, 0x70, 0xFF), kBlackNavy);
constexpr auto kBlackTabLeft = ChosenLeft(Solid(kBlackPanel), kBlackEdge, false);
constexpr auto kBlackTabMiddle = ChosenMiddle(Solid(kBlackPanel), kBlackEdge, false);
constexpr auto kBlackTabRight = ChosenRight(Solid(kBlackPanel), kBlackEdge, false);
constexpr auto kBlackIdleLeft = OtherLeft(Solid(kBlack), kBlackEdge, false);
constexpr auto kBlackIdleMiddle = OtherMiddle(Solid(kBlack), kBlackEdge, false);
constexpr auto kBlackIdleRight = OtherRight(Solid(kBlack), kBlackEdge, false);
constexpr auto kBlackClose = FramedGlyph(Glyph(kBlackX, kBlackTeal, kBlackNavy), kBlackEdge, kBlackEdge);
constexpr auto kBlackCloseHeld = FramedGlyph(Glyph(kBlackX, kBlackNavy, kBlackNavy), kBlackEdge, kBlackEdge);
constexpr auto kBlackMore = FramedGlyph(Glyph(kBlackUp, kBlackTeal, kBlackNavy), kBlackEdge, kBlackEdge);
constexpr auto kBlackMoreHeld = FramedGlyph(Glyph(kBlackUp, kBlackNavy, kBlackNavy), kBlackEdge, kBlackEdge);
constexpr auto kBlackLess = FramedGlyph(Glyph(kBlackDown, kBlackTeal, kBlackNavy), kBlackEdge, kBlackEdge);
constexpr auto kBlackLessHeld = FramedGlyph(Glyph(kBlackDown, kBlackNavy, kBlackNavy), kBlackEdge, kBlackEdge);

constexpr Theme kMinimalistBlackTheme = {
    .name = "Minimalist Black",
    .view = {nullptr, kBlack},
    .top = Stack(kBlackTitle, kBlack),        // and ViewTopBackground_Selected, the same
    .border = 1.0f,
    .border_resizeable = 4.0f,
    .frame = FrameStyle::Solid,
    .frame_colour = IM_COL32(0x2F, 0x41, 0x42, 0xFF),
    .title_bar = 12.0f,
    .title_rule = 0.0f,
    .title_rule_fill = {nullptr, kBlack},
    .icon_x = 1.0f, .icon_y = 1.0f, .icon_size = 10.0f,
    .title_x = 13.0f, .title_y = -2.0f,
    .title_centred = false,
    .title_points = 8.0f,
    .title_bold = false,
    .title_text = IM_COL32(0x23, 0x42, 0x45, 0xFF), .title_text_focused = kBlackTeal,
    .button_size = 10.0f, .buttons_left = 1.0f, .buttons_down = 1.0f, .button_spacing = 1.0f,
    .alpha_buttons = true,
    .close_up = Stack(kBlackClose, kBlack),
    .close_down = Stack(kBlackCloseHeld, kBlack),
    .view_text = kBlackTeal,
    .list_text = kBlackPale,
    .button_face = kBlackBand, .button_shadow = kBlackEdge, .button_highlight = kBlackEdge, .button_text = kBlackTeal,
    .text_box = {nullptr, kBlackPanel},
    .text_box_text = kBlackPale,
    .check_off = Stack(kBlackCheckOff, kBlackPanel),
    .check_on = Stack(kBlackCheckOn, kBlackPanel),
    .list = {nullptr, kBlackPanel},
    .scroll_up = Stack(kBlackScrollUp, kBlack),
    .scroll_down = Stack(kBlackScrollDown, kBlack),
    .scroll_track = {nullptr, kBlack},
    .progress_filled = IM_COL32(0, 0, 0xFF, 0xFF), .progress_empty = kBlack,
    .tabs = TabStyle::Fills,
    .tab_colour = kBlackPanel,
    .tab_text_selected = kBlackTeal, .tab_text_idle = kBlackEdge,
    .tab_points = 7.0f,
    .tab_bold = false,
    .tab_padding = 10.0f,
    .tab_cap = 2.0f,
    .tab_cap_right = 3.0f,
    .title_halo = kBlack,
    .title_halo_focused = kBlackNavy,
    .text_halo = kBlackNavy,
    .text_box_rule = true,
    .button_up = Stack(kBlackFace, kBlackBand),
    .button_down = Stack(kBlackFaceDown, kBlackBand),
    .alpha_up = {Stack(kBlackMore, kBlack), Stack(kBlackMoreHeld, kBlack)},
    .alpha_down = {Stack(kBlackLess, kBlack), Stack(kBlackLessHeld, kBlack)},
    .slider_nub = Stack(kBlackNub, IM_COL32(0x70, 0x70, 0x70, 0xFF)),
    .slider_outer = kBlackPanel,
    .slider_inner = kBlackEdge,
    .combo_selected = {nullptr, kBlackChosen},
    .combo_selecting = {nullptr, kBlackChosen},
    .combo_arrow = Stack(kBlackComboArrow, kBlack),
    .scroll_button = 12.0f,
    .bubble_cap = 2.0f,
    .bubble_top = Stack(kBlackBubbleTop, kBlackBand),
    .bubble_middle = Stack(kBlackBubbleMiddle, kBlackBand),
    .bubble_hover = Stack(kBlackBubbleMiddle, kBlackBand),
    .bubble_drag = Stack(kBlackBubbleMiddle, kBlackBand),
    .bubble_bottom = Stack(kBlackBubbleBottom, kBlackBand),
    .tab_height = 13.0f,
    .tab_halo_selected = kBlackNavy,          // and TabTextFontShadowSize_Deselected 0
    .tab_selected = {Stack(kBlackTabLeft, kBlackPanel), Stack(kBlackTabMiddle, kBlackPanel), Stack(kBlackTabRight, kBlackPanel)},
    .tab_idle = {Stack(kBlackIdleLeft, kBlack), Stack(kBlackIdleMiddle, kBlack), Stack(kBlackIdleRight, kBlack)},
    .tooltip_border = kBlackEdge,             // TooltipBorder*: ButtonShadow
    .menu_back = {nullptr, kBlack},
    .menu_text = kBlackTeal,
    .menu_hover = kBlackChosen,
    .bar_underlay = kBlackEdge,
};

// VVS's Minimalist Transparent: black glass, most of it barely there, edged in sea green.
constexpr ImU32 kGlassFaint = IM_COL32(0, 0, 0, 70);                 // color
constexpr ImU32 kGlassLight = IM_COL32(0, 0, 0, 100);                // color2
constexpr ImU32 kGlassDark = IM_COL32(0, 0, 0, 180);                 // color3
constexpr ImU32 kGlassText = IM_COL32(0xDC, 0xDC, 0xDC, 0xFF);       // value, value2
constexpr ImU32 kGlassShade = IM_COL32(0x00, 0x80, 0x80, 128);       // color5
constexpr ImU32 kGlassLit = IM_COL32(0x84, 0xBD, 0xAA, 128);         // color6
constexpr ImU32 kGlassTeal = IM_COL32(0x00, 0x80, 0x80, 80);         // color8
constexpr ImU32 kGlassChosen = IM_COL32(0xD4, 0xD0, 0xC8, 0xFF);     // color4
constexpr ImU32 kGlassShadow = IM_COL32(0, 0, 0, 220);               // Color.Black at shadow alpha 220
constexpr auto kGlassCheckOff = Box(kGlassLight, kGlassLit, kGlassShade, Nothing());
constexpr auto kGlassCheckOn = Box(kGlassLight, kGlassLit, kGlassShade, Glyph(kTick, kWhite, kBlack));
constexpr auto kGlassScrollUp = ArrowButton(Solid(kGlassTeal), Nothing(), kGlassLit, kGlassShade, Glyph(kArrowUp, kWhite, kBlack));
constexpr auto kGlassScrollDown = ArrowButton(Solid(kGlassTeal), Nothing(), kGlassLit, kGlassShade, Glyph(kArrowDown, kWhite, kBlack));
constexpr std::array<Layer, 1> kGlassTrack = {Checks(kGlassLight, kGlassFaint)};
constexpr auto kGlassBubbleTop = BubbleTop(Solid(kGlassTeal), Nothing(), kGlassLit, kGlassShade);
constexpr auto kGlassBubbleMiddle = BubbleMiddle(Solid(kGlassTeal), Nothing(), kGlassLit, kGlassShade);
constexpr auto kGlassBubbleBottom = BubbleBottom(Solid(kGlassTeal), Nothing(), kGlassLit, kGlassShade);
constexpr auto kGlassTabLeft = ChosenLeft(Solid(kGlassLight), kGlassShade, false);
constexpr auto kGlassTabMiddle = ChosenMiddle(Solid(kGlassLight), kGlassShade, false);
constexpr auto kGlassTabRight = ChosenRight(Solid(kGlassLight), kGlassShade, false);
constexpr auto kGlassIdleLeft = OtherLeft(Solid(kGlassFaint), kGlassShade, false);
constexpr auto kGlassIdleMiddle = OtherMiddle(Solid(kGlassFaint), kGlassShade, false);
constexpr auto kGlassIdleRight = OtherRight(Solid(kGlassFaint), kGlassShade, false);

constexpr Theme kMinimalistTransparentTheme = {
    .name = "Minimalist Transparent",
    .view = {nullptr, kGlassFaint},
    .top = {nullptr, kGlassFaint},
    .border = 1.0f,
    .border_resizeable = 3.0f,
    .frame = FrameStyle::Solid,
    .frame_colour = kClear,                   // ViewBorder*: Color.Transparent
    .title_bar = 12.0f,
    .title_rule = 0.0f,
    .title_rule_fill = {nullptr, kClear},
    .icon_x = 1.0f, .icon_y = 1.0f, .icon_size = 10.0f,
    .title_x = 13.0f, .title_y = -2.0f,
    .title_centred = false,
    .title_points = 8.0f,
    .title_bold = false,
    .title_text = kGlassText, .title_text_focused = kGlassText,
    .button_size = 10.0f, .buttons_left = 1.0f, .buttons_down = 1.0f, .button_spacing = 1.0f,
    .alpha_buttons = true,
    .close_up = Stack(kCloseLayers, kBlack),
    .close_down = Stack(kCloseHeldLayers, kBlack),
    .view_text = kGlassText,
    .list_text = kGlassText,
    .button_face = kGlassTeal, .button_shadow = kGlassShade, .button_highlight = kGlassLit, .button_text = kGlassText,
    .text_box = {nullptr, kGlassLight},
    .text_box_text = kGlassText,
    .check_off = Stack(kGlassCheckOff, kGlassLight),
    .check_on = Stack(kGlassCheckOn, kGlassLight),
    .list = {nullptr, kGlassLight},
    .scroll_up = Stack(kGlassScrollUp, kGlassTeal),
    .scroll_down = Stack(kGlassScrollDown, kGlassTeal),
    .scroll_track = Stack(kGlassTrack, kGlassFaint),
    .progress_filled = IM_COL32(0, 0, 0xFF, 0xFF), .progress_empty = kBlack,
    .tabs = TabStyle::Fills,
    .tab_colour = kGlassFaint,
    .tab_text_selected = IM_COL32(0x00, 0x80, 0x80, 0xFF), .tab_text_idle = kGlassText,
    .tab_points = 7.0f,
    .tab_bold = false,
    .tab_padding = 10.0f,
    .tab_cap = 2.0f,
    .tab_cap_right = 3.0f,
    .top_focused = {nullptr, kGlassTeal},
    .title_halo = kGlassShadow,
    .title_halo_focused = kGlassShadow,
    .black_under = false,
    .text_halo = kGlassShadow,
    .text_box_rule = true,
    .button_up = {nullptr, kGlassTeal},
    .button_down = {nullptr, kGlassTeal},
    .combo_box = {nullptr, kGlassDark},
    .combo_list = {nullptr, kGlassDark},
    .combo_selected = {nullptr, kGlassChosen},
    .combo_selecting = {nullptr, kGlassChosen},
    .scroll_button = 12.0f,
    .bubble_cap = 2.0f,
    .bubble_top = Stack(kGlassBubbleTop, kGlassTeal),
    .bubble_middle = Stack(kGlassBubbleMiddle, kGlassTeal),
    .bubble_hover = Stack(kGlassBubbleMiddle, kGlassTeal),
    .bubble_drag = Stack(kGlassBubbleMiddle, kGlassTeal),
    .bubble_bottom = Stack(kGlassBubbleBottom, kGlassTeal),
    .tab_height = 13.0f,
    .tab_halo_selected = kGlassShadow,
    .tab_halo_idle = kGlassShadow,
    .tab_selected = {Stack(kGlassTabLeft, kGlassLight), Stack(kGlassTabMiddle, kGlassLight), Stack(kGlassTabRight, kGlassLight)},
    .tab_idle = {Stack(kGlassIdleLeft, kGlassFaint), Stack(kGlassIdleMiddle, kGlassFaint), Stack(kGlassIdleRight, kGlassFaint)},
    .tooltip_border = kGlassShade,
    // A menu item's MenuLeftAreaFill and MenuRightAreaFill, both color3, filled over the view's
    // faint glass and - as every colour fill in a VVS view did - replaced it.
    .menu_back = {nullptr, kGlassDark},
    .menu_text = kGlassText,
    .menu_hover = kGlassChosen,
    .menu_left = kGlassDark,
    .bar_underlay = kGlassShade,
};

// "Minimalist H.S.", VVS's hot-dog stand: red and yellow, set only by the H.S. button on the
// VVS bar's title strip and kept out of every list of themes.
constexpr ImU32 kHsYellow = IM_COL32(0xFF, 0xFF, 0x00, 0xFF);   // color2
constexpr ImU32 kHsDark = IM_COL32(0x50, 0x00, 0x00, 0xFF);     // color3, color12
constexpr ImU32 kHsLight = IM_COL32(0xAF, 0x00, 0x00, 0xFF);    // color4, color10
constexpr ImU32 kHsMiddle = IM_COL32(0x7F, 0x00, 0x00, 0xFF);   // color9, color11
constexpr std::array<Layer, 2> kHsTitle = {Solid(kHsLight, 0.0f, 0.0f, 1.0f, 0.5f), Solid(kHsDark, 0.0f, 0.5f, 1.0f, 0.5f)};
constexpr std::array<Layer, 3> kHsFace = {Solid(kHsMiddle), Solid(kHsLight, 0.0f, 0.0f, 1.0f, 0.47f), Solid(kHsDark, 0.0f, 0.47f + 0.06f, 1.0f, 0.47f)};
constexpr std::array<Layer, 3> kHsFaceDown = {Solid(kHsMiddle), Solid(kHsDark, 0.0f, 0.0f, 1.0f, 0.47f), Solid(kHsLight, 0.0f, 0.47f + 0.06f, 1.0f, 0.47f)};
constexpr auto kHsCheckOff = EdgedBox(kHsYellow, kHsLight, kHsDark, Nothing());
constexpr auto kHsCheckOn = EdgedBox(kHsYellow, kHsLight, kHsDark, Glyph(kTick, kBlack, kHsYellow));
constexpr auto kHsScrollUp = ArrowButton(Solid(kHsDark, 0.0f, 0.0f, 1.0f, 0.7f), Solid(kHsLight, 0.0f, 0.7f, 1.0f, 0.3f), kHsLight, kHsDark,
                                         Glyph(kArrowUp, kWhite, kBlack));
constexpr auto kHsScrollDown = ArrowButton(Solid(kHsLight, 0.0f, 0.0f, 1.0f, 0.3f), Solid(kHsDark, 0.0f, 0.3f, 1.0f, 0.7f), kHsLight, kHsDark,
                                           Glyph(kArrowDown, kWhite, kBlack));
constexpr auto kHsComboArrow = ArrowButton(Solid(kHsLight, 0.0f, 0.0f, 1.0f, 0.3f), Solid(kHsDark, 0.0f, 0.3f, 1.0f, 0.7f), kHsLight, kHsDark,
                                           Glyph(kArrowDown, kWhite, kBlack), kPixel16, 15.0f * kPixel16);
constexpr auto kHsBubbleTop = BubbleTop(Solid(kHsDark, 0.0f, 0.0f, 0.5f, 1.0f), Solid(kHsLight, 0.5f, 0.0f, 0.5f, 1.0f), kHsLight, kHsDark);
constexpr auto kHsBubbleMiddle = BubbleMiddle(Solid(kHsDark, 0.0f, 0.0f, 0.5f, 1.0f), Solid(kHsLight, 0.5f, 0.0f, 0.5f, 1.0f), kHsLight, kHsDark);
constexpr auto kHsBubbleBottom = BubbleBottom(Solid(kHsDark, 0.0f, 0.0f, 0.5f, 1.0f), Solid(kHsLight, 0.5f, 0.0f, 0.5f, 1.0f), kHsLight, kHsDark);
constexpr auto kHsNub = Nub(kHsYellow, kBlack);
constexpr auto kHsTabLeft = ChosenLeft(Solid(kHsYellow), kHsDark, false);
constexpr auto kHsTabMiddle = ChosenMiddle(Solid(kHsYellow), kHsDark, false);
constexpr auto kHsTabRight = ChosenRight(Solid(kHsYellow), kHsDark, false);
constexpr auto kHsIdleLeft = OtherLeft(Solid(kRed), kHsDark, false);
constexpr auto kHsIdleMiddle = OtherMiddle(Solid(kRed), kHsDark, false);
constexpr auto kHsIdleRight = OtherRight(Solid(kRed), kHsDark, false);
constexpr auto kHsClose = FramedGlyph(Glyph(kBlackX, kWhite, kBlack), kHsLight, kHsDark);
constexpr auto kHsCloseHeld = FramedGlyph(Glyph(kBlackX, kBlack, kBlack), kHsLight, kHsDark);
constexpr auto kHsMore = FramedGlyph(Glyph(kBlackUp, kWhite, kBlack), kHsLight, kHsDark);
constexpr auto kHsMoreHeld = FramedGlyph(Glyph(kBlackUp, kBlack, kBlack), kHsLight, kHsDark);
constexpr auto kHsLess = FramedGlyph(Glyph(kBlackDown, kWhite, kBlack), kHsLight, kHsDark);
constexpr auto kHsLessHeld = FramedGlyph(Glyph(kBlackDown, kBlack, kBlack), kHsLight, kHsDark);

constexpr Theme kHotDogStandTheme = {
    .name = "Minimalist H.S.",
    .view = {nullptr, kRed},
    .top = Stack(kHsTitle, kHsLight),
    .border = 1.0f,
    .border_resizeable = 4.0f,
    .frame = FrameStyle::Solid,
    .frame_colour = kBlack,
    .title_bar = 12.0f,
    .title_rule = 0.0f,
    .title_rule_fill = {nullptr, kBlack},
    .icon_x = 1.0f, .icon_y = 1.0f, .icon_size = 10.0f,
    .title_x = 13.0f, .title_y = -2.0f,
    .title_centred = false,
    .title_points = 8.0f,
    .title_bold = false,
    .title_text = kWhite, .title_text_focused = kWhite,
    .button_size = 10.0f, .buttons_left = 1.0f, .buttons_down = 1.0f, .button_spacing = 1.0f,
    .alpha_buttons = true,
    .close_up = Stack(kHsClose, kHsMiddle),
    .close_down = Stack(kHsCloseHeld, kHsMiddle),
    .view_text = kWhite,
    .list_text = kBlack,
    .button_face = kHsMiddle, .button_shadow = kHsDark, .button_highlight = kHsLight, .button_text = kWhite,
    .text_box = {nullptr, kHsYellow},
    .text_box_text = kBlack,
    .check_off = Stack(kHsCheckOff, kHsYellow),
    .check_on = Stack(kHsCheckOn, kHsYellow),
    .list = {nullptr, kHsYellow},
    .scroll_up = Stack(kHsScrollUp, kHsDark),
    .scroll_down = Stack(kHsScrollDown, kHsDark),
    .scroll_track = {nullptr, kBlack},
    .progress_filled = IM_COL32(0, 0, 0xFF, 0xFF), .progress_empty = kBlack,
    .tabs = TabStyle::Fills,
    .tab_colour = kRed,
    .tab_text_selected = kBlack, .tab_text_idle = kWhite,
    .tab_points = 7.0f,
    .tab_bold = false,
    .tab_padding = 10.0f,
    .tab_cap = 2.0f,
    .tab_cap_right = 3.0f,
    .top_focused = {nullptr, kBlack},
    .title_halo = kBlack,
    .title_halo_focused = kBlack,
    .text_box_rule = true,
    .button_up = Stack(kHsFace, kHsMiddle),
    .button_down = Stack(kHsFaceDown, kHsMiddle),
    .alpha_up = {Stack(kHsMore, kHsMiddle), Stack(kHsMoreHeld, kHsMiddle)},
    .alpha_down = {Stack(kHsLess, kHsMiddle), Stack(kHsLessHeld, kHsMiddle)},
    .slider_nub = Stack(kHsNub, kHsYellow),
    .slider_outer = kBlackPanel,              // -14935012, as Minimalist Black's
    .slider_inner = kHsLight,
    .combo_selected = {nullptr, kHsLight},
    .combo_selecting = {nullptr, kHsDark},
    .combo_arrow = Stack(kHsComboArrow, kHsDark),
    .scroll_button = 12.0f,
    .bubble_cap = 2.0f,
    .bubble_top = Stack(kHsBubbleTop, kHsLight),
    .bubble_middle = Stack(kHsBubbleMiddle, kHsLight),
    .bubble_hover = Stack(kHsBubbleMiddle, kHsLight),
    .bubble_drag = Stack(kHsBubbleMiddle, kHsLight),
    .bubble_bottom = Stack(kHsBubbleBottom, kHsLight),
    .tab_height = 13.0f,
    .tab_selected = {Stack(kHsTabLeft, kHsYellow), Stack(kHsTabMiddle, kHsYellow), Stack(kHsTabRight, kHsYellow)},
    .tab_idle = {Stack(kHsIdleLeft, kRed), Stack(kHsIdleMiddle, kRed), Stack(kHsIdleRight, kRed)},
    .tooltip_border = kHsDark,                // TooltipBorder*: ButtonShadow
    .menu_back = {nullptr, kRed},
    .menu_text = kWhite,
    .menu_hover = kHsLight,
    .bar_underlay = kHsDark,
};

// The themes a window's menu offers, and the VVS bar's "ab" steps through, in the order VVS
// registered them.
constexpr const Theme* kThemes[] = {&kMinimalistTheme, &kFloatTheme, &kMinimalistTransparentTheme,
                                    &kDecalTheme,      &kMinimalistBlackTheme, &kMinimalistGreenTheme};

// The theme of a window whose host names none. VVS's own default is Float - the second
// theme it lists, which the registry never overrode on this machine - but the host resolves
// that for each view, along with what the player chose in vvs.s3db. A host that sends no
// theme predates themes, and every window it knew looked like Decal.
const Theme& DefaultTheme() { return kDecalTheme; }

// A theme by name: one of the six, or the hot-dog stand, which VVS answered to by name too.
const Theme& ThemeNamed(const std::string& name) {
    for (const Theme* theme : kThemes)
        if (_stricmp(theme->name, name.c_str()) == 0) return *theme;
    if (_stricmp(kHotDogStandTheme.name, name.c_str()) == 0) return kHotDogStandTheme;
    return DefaultTheme();
}

// Metrics every theme shares, in Decal pixels.
constexpr float kBubbleMin = 12.0f;      // VScrollBarBarMinHeight
constexpr float kNubWidth = 7.0f;        // SliderNubWidth
constexpr float kNubHeight = 12.0f;      // SliderNubHeight
constexpr float kCheckBox = 13.0f;       // HudCheckBox draws its box 13 by 13, whatever the image
constexpr float kCheckTextX = 17.0f;     // and its label 17 pixels in
constexpr float kRowHeight = 16.0f;      // one list row: an icon's height

// VVS's alpha: 255 opaque, stepped by 25 from the title bar, never below 40.
constexpr int kAlphaStep = 25;
constexpr int kAlphaMin = 40;
constexpr int kAlphaMax = 255;

// What VVS adds to each list column's declared width. Not in the theme; read off Virindi
// Tank's own Monsters page, whose headings are labels placed by hand over the list: its
// sixteen-pixel check columns sit on a twenty-pixel pitch, and every text heading is centred
// over its column only when each column is four pixels wider than it says.
constexpr float kColumnPadding = 4.0f;
constexpr float kTextBoxCap = 4.0f;      // the end caps of the text box image, in its pixels

// Font sizes are in points, as the themes give them; VVS asked Direct3D for them at 96 DPI.
// Decal's bar, which is no VVS window, is lettered at the Decal theme's DefaultTextFontSize.
constexpr float kDefaultPoints = 8.0f;

// One Decal pixel to one screen pixel, as the standard client draws Decal and Virindi View
// Service at 1920 by 1080 - which is what the player compared this against.
constexpr float kScale = 1.0f;

float PointsToPixels(float points) { return points * 96.0f / 72.0f; }

// ---------------------------------------------------------------------------------------
// What is remembered between frames, per window: the player's hands, never the session.

struct EditMemory {
    std::string text;
    double number = 0.0;
    int frame = 0;
};

struct NotebookMemory {
    int active = 0;
    int last_published = -1;
};

struct WindowMemory {
    std::map<std::string, NotebookMemory> notebooks;
    std::map<std::string, float> scroll;       // list scroll offsets, in rows
    std::map<std::string, EditMemory> edits;   // text boxes and sliders being changed

    // What the player chose from the title bar, kept in the overlay's ini beside where the
    // window was left - as VVS kept them per machine, in its StoredViewInfo table. Unset
    // means whatever the host says the view starts as.
    int alpha = kAlphaMax;
    std::string theme;
    std::optional<bool> ghosted;
    std::optional<bool> click_through;

    // Which screen edges a hudified window was pushed against and stays against - VVS's
    // GhostSticky - as "L", "R", "T" and "B".
    std::string stuck;
};

std::map<std::string, WindowMemory>& Memory() {
    static std::map<std::string, WindowMemory> memory;
    return memory;
}

// Whether the player is holding the key that shows a hudified window's frame: VVS's left
// Ctrl. Set by the caller once a frame, since only it knows how the game takes its keys.
bool g_reveal = false;

// ---------------------------------------------------------------------------------------
// Drawing helpers.

// Everything drawn for one window, in one place: where the view's origin is on screen, how
// large a Decal pixel is, and who to stamp on a command.
struct Frame {
    const std::string& owner;
    ImDrawList* draw;
    ImVec2 origin;  // screen position of the view's (0, 0)
    float s;        // screen pixels per Decal pixel
    std::vector<Command>& commands;
    WindowMemory& memory;
    const Theme& t;
};

ImVec2 Screen(const Frame& f, float x, float y) { return ImVec2(f.origin.x + x * f.s, f.origin.y + y * f.s); }

// Style alpha applies to everything drawn through GetColorU32, which is how the window's
// transparency buttons reach every image and line without each one being told.
ImU32 Col(ImU32 colour) { return ImGui::GetColorU32(colour); }

ImU32 FromArgb(int64_t argb) {
    const auto v = static_cast<uint32_t>(argb);
    return IM_COL32((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, (v >> 24) & 0xFF);
}

ImFont* FontFor(bool bold) {
    const DecalFonts& fonts = GetDecalFonts();
    ImFont* font = bold ? fonts.bold : fonts.regular;
    return font != nullptr ? font : ImGui::GetFont();
}

void DrawImage(ImDrawList* draw, const Texture* tex, ImVec2 a, ImVec2 b, ImVec2 uv0 = ImVec2(0, 0), ImVec2 uv1 = ImVec2(1, 1)) {
    if (tex != nullptr) draw->AddImage(tex->ref, a, b, uv0, uv1, Col(IM_COL32_WHITE));
}

// Repeats an image across a rectangle at the scale, cutting the last row and column short
// rather than squashing them - which is how a tiled background looks in the original.
void TileTexture(ImDrawList* draw, const Texture* tex, ImVec2 a, ImVec2 b, float s, ImU32 stand_in) {
    if (tex == nullptr || tex->width <= 0.0f || tex->height <= 0.0f) {
        draw->AddRectFilled(a, b, Col(stand_in));
        return;
    }

    const float tw = tex->width * s;
    const float th = tex->height * s;
    for (float y = a.y; y < b.y; y += th) {
        const float h = std::min(th, b.y - y);
        for (float x = a.x; x < b.x; x += tw) {
            const float w = std::min(tw, b.x - x);
            draw->AddImage(tex->ref, ImVec2(x, y), ImVec2(x + w, y + h), ImVec2(0, 0), ImVec2(w / tw, h / th), Col(IM_COL32_WHITE));
        }
    }
}

void Tile(ImDrawList* draw, const char* key, ImVec2 a, ImVec2 b, float s, ImU32 stand_in) {
    TileTexture(draw, FindTexture(key), a, b, s, stand_in);
}

// An image in nine pieces: its corners, `corner` pixels square, as they are, its edges
// stretched along and its middle stretched both ways - Minimalist Green's buttons, which VVS
// drew with nine DrawPortalImageEx calls, or with one, the image whole, on a button too small
// for its corners.
void NineSlice(ImDrawList* draw, const Texture* tex, ImVec2 a, ImVec2 b, float corner, float s) {
    if (tex == nullptr || tex->width <= corner * 2.0f || tex->height <= corner * 2.0f) return;
    if ((b.x - a.x) <= corner * 2.0f * s || (b.y - a.y) <= corner * 2.0f * s) {
        DrawImage(draw, tex, a, b);
        return;
    }
    const float c = corner * s;
    const float xs[4] = {a.x, a.x + c, b.x - c, b.x};
    const float ys[4] = {a.y, a.y + c, b.y - c, b.y};
    const float us[4] = {0.0f, corner / tex->width, 1.0f - corner / tex->width, 1.0f};
    const float vs[4] = {0.0f, corner / tex->height, 1.0f - corner / tex->height, 1.0f};
    for (int row = 0; row < 3; ++row)
        for (int column = 0; column < 3; ++column)
            DrawImage(draw, tex, ImVec2(xs[column], ys[row]), ImVec2(xs[column + 1], ys[row + 1]), ImVec2(us[column], vs[row]),
                      ImVec2(us[column + 1], vs[row + 1]));
}

bool IsSet(const Fill& fill) { return fill.image != nullptr || fill.layers != nullptr || fill.colour != kClear; }

const Fill& Either(const Fill& fill, const Fill& otherwise) { return IsSet(fill) ? fill : otherwise; }

// The part of a rectangle a layer covers, as HudViewDrawStyle worked it out: in whole Decal
// pixels, truncated, and cut off at the rectangle's right and bottom. False if it is empty.
bool Part(const Layer& layer, ImVec2 a, ImVec2 b, float s, ImVec2& pa, ImVec2& pb) {
    const int width = static_cast<int>(std::lround((b.x - a.x) / s));
    const int height = static_cast<int>(std::lround((b.y - a.y) / s));
    int x = 0, y = 0, w = width, h = height;
    switch (layer.edge) {
        case Edge::Left: w = 1; break;
        case Edge::Top: h = 1; break;
        case Edge::Right: x = width - 1; w = 1; break;
        case Edge::Bottom: y = height - 1; h = 1; break;
        case Edge::Inset: x = 1; y = 1; w = width - 2; h = height - 2; break;
        case Edge::None:
            x = static_cast<int>(layer.x * static_cast<float>(width));
            y = static_cast<int>(layer.y * static_cast<float>(height));
            w = static_cast<int>(static_cast<float>(width) * layer.w);
            h = static_cast<int>(static_cast<float>(height) * layer.h);
            x = std::max(x, 0);
            y = std::max(y, 0);
            if (x + w > width) w = width - x;
            if (y + h > height) h = height - y;
            break;
    }
    if (w <= 0 || h <= 0) return false;
    pa = ImVec2(a.x + static_cast<float>(x) * s, a.y + static_cast<float>(y) * s);
    pb = ImVec2(pa.x + static_cast<float>(w) * s, pa.y + static_cast<float>(h) * s);
    return true;
}

// A fill's stack of layers, each in turn over its part of the rectangle. False if an image in
// it has not arrived, so the caller can draw its stand-in.
bool DrawLayers(ImDrawList* draw, const Fill& fill, ImVec2 a, ImVec2 b, float s) {
    bool whole = true;
    for (size_t i = 0; i < fill.count; ++i) {
        const Layer& layer = fill.layers[i];
        ImVec2 pa, pb;
        if (!Part(layer, a, b, s, pa, pb)) continue;
        switch (layer.piece) {
            case Piece::Colour:
                draw->AddRectFilled(pa, pb, Col(layer.colour));
                break;
            case Piece::Image: {
                const bool redrawn = layer.colour != kClear || layer.colour2 != kClear;
                const Texture* tex = redrawn ? FindOutlinedTexture(layer.image, layer.colour, layer.colour2) : FindTexture(layer.image);
                if (tex == nullptr) whole = false;
                DrawImage(draw, tex, pa, pb);
                break;
            }
            case Piece::Tile:
                draw->PushClipRect(pa, pb, true);
                Tile(draw, layer.image, pa, pb, s, layer.colour);
                draw->PopClipRect();
                break;
            case Piece::Ramp:
                draw->AddRectFilledMultiColor(pa, pb, Col(layer.colour), Col(layer.colour2), Col(layer.colour2), Col(layer.colour));
                break;
            case Piece::Checker:
                draw->PushClipRect(pa, pb, true);
                TileTexture(draw, FindCheckerTexture(layer.colour, layer.colour2, 12, 600), pa, pb, s, layer.colour2);
                draw->PopClipRect();
                break;
            case Piece::Nine: {
                const Texture* tex = FindTexture(layer.image);
                if (tex == nullptr) {
                    draw->AddRectFilled(pa, pb, Col(layer.colour));
                    whole = false;
                }
                NineSlice(draw, tex, pa, pb, 5.0f, s);
                break;
            }
        }
    }
    return whole;
}

// A theme's surface over a rectangle, as VVS's FloodFill laid it: its image tiled, its colour,
// or its stack. The stand-in for a missing image is the colour, so a surface is never left
// unpainted.
void Paint(ImDrawList* draw, const Fill& fill, ImVec2 a, ImVec2 b, float s) {
    if (b.x <= a.x || b.y <= a.y) return;
    if (fill.layers != nullptr) {
        DrawLayers(draw, fill, a, b, s);
        return;
    }
    if (fill.image == nullptr) {
        draw->AddRectFilled(a, b, Col(fill.colour));
        return;
    }
    ImGui::PushClipRect(a, b, true);
    Tile(draw, fill.image, a, b, s, fill.colour);
    ImGui::PopClipRect();
}

// The same, as VVS's FillSingle laid it: the image once, stretched over the rectangle. False
// if the image has not arrived - nothing is drawn for it, and the caller draws its own stand-in.
bool PaintOnce(ImDrawList* draw, const Fill& fill, ImVec2 a, ImVec2 b, float s) {
    if (b.x <= a.x || b.y <= a.y) return true;
    if (fill.layers != nullptr) return DrawLayers(draw, fill, a, b, s);
    if (fill.image == nullptr) {
        draw->AddRectFilled(a, b, Col(fill.colour));
        return true;
    }
    const Texture* tex = FindTexture(fill.image);
    DrawImage(draw, tex, a, b);
    return tex != nullptr;
}

// An image in three pieces: fixed ends, stretched middle. How the text box and the tabs
// are drawn at any width.
void ThreeSlice(ImDrawList* draw, const Texture* tex, ImVec2 a, ImVec2 b, float cap_left, float cap_right, float s) {
    if (tex == nullptr || tex->width <= cap_left + cap_right) return;

    const float l = cap_left * s;
    const float r = cap_right * s;
    const float ul = cap_left / tex->width;
    const float ur = 1.0f - cap_right / tex->width;

    DrawImage(draw, tex, a, ImVec2(a.x + l, b.y), ImVec2(0, 0), ImVec2(ul, 1));
    DrawImage(draw, tex, ImVec2(a.x + l, a.y), ImVec2(b.x - r, b.y), ImVec2(ul, 0), ImVec2(ur, 1));
    DrawImage(draw, tex, ImVec2(b.x - r, a.y), b, ImVec2(ur, 0), ImVec2(1, 1));
}

ImVec2 Measure(ImFont* font, float size, std::string_view text) {
    return font->CalcTextSizeA(size, FLT_MAX, 0.0f, text.data(), text.data() + text.size());
}

// Host strings are data. Drawn through AddText, never through a printf-style call.
void Text(ImDrawList* draw, ImFont* font, float size, ImVec2 pos, ImU32 colour, std::string_view text,
          const ImVec4* clip = nullptr, float wrap = 0.0f) {
    if (text.empty()) return;
    draw->AddText(font, size, pos, Col(colour), text.data(), text.data() + text.size(), wrap, clip);
}

// Text with a theme's font shadow of size one round it, where the theme gives one: VVS grew
// each glyph by a pixel every way, across and diagonally, in the shadow's colour and at its
// alpha, and drew the text over that.
void HaloText(ImDrawList* draw, ImFont* font, float size, ImVec2 pos, ImU32 colour, ImU32 halo, std::string_view text,
              const ImVec4* clip = nullptr, float wrap = 0.0f) {
    if ((halo & IM_COL32_A_MASK) != 0) {
        for (int dy = -1; dy <= 1; ++dy)
            for (int dx = -1; dx <= 1; ++dx)
                if (dx != 0 || dy != 0)
                    Text(draw, font, size, ImVec2(pos.x + static_cast<float>(dx) * kScale, pos.y + static_cast<float>(dy) * kScale), halo,
                         text, clip, wrap);
    }
    Text(draw, font, size, pos, colour, text, clip, wrap);
}

void Emit(const Frame& f, const char* name, const std::string& control, const std::string& value,
          const std::string& row = std::string()) {
    Command command;
    command.name = name;
    command.owner = f.owner;
    command.control_id = control;
    command.value = value;
    command.row_id = row;
    f.commands.push_back(std::move(command));
}

std::string FormatInteger(double value) {
    char buffer[32];
    const auto result = std::to_chars(buffer, buffer + sizeof(buffer), static_cast<long long>(std::llround(value)));
    return std::string(buffer, result.ptr);
}

double ParseNumber(const std::string& text, double fallback) {
    double number = 0.0;
    const auto result = std::from_chars(text.data(), text.data() + text.size(), number);
    if (result.ec != std::errc() || !std::isfinite(number)) return fallback;
    return number;
}

std::string MemoryKey(const std::string& name) { return name; }

// An invisible item over a rectangle, for clicks and drags. Zero-sized items assert, so a
// control with no area is simply not interactive.
bool Hit(const char* id, ImVec2 a, ImVec2 b) {
    if (b.x - a.x < 1.0f || b.y - a.y < 1.0f) return false;
    ImGui::SetCursorScreenPos(a);
    return ImGui::InvisibleButton(id, ImVec2(b.x - a.x, b.y - a.y));
}

// ---------------------------------------------------------------------------------------
// Controls.

struct Rect {
    float x, y, w, h;  // Decal pixels, relative to the view
};

void DrawControl(Frame& f, const ViewControl& c, const Rect& parent, int ordinal);

Rect Place(const ViewControl& c, const Rect& parent) {
    // A layout with no size of its own fills its parent - that is what a notebook page's
    // FixedLayout is, and every view's root.
    Rect r;
    r.x = parent.x + static_cast<float>(c.x);
    r.y = parent.y + static_cast<float>(c.y);
    r.w = c.w > 0 ? static_cast<float>(c.w) : std::max(0.0f, parent.w - static_cast<float>(c.x));
    r.h = c.h > 0 ? static_cast<float>(c.h) : std::max(0.0f, parent.h - static_cast<float>(c.y));
    return r;
}

// Decal's XML gives a label's size in its own units; VVS read it as points after scaling by
// this, so fontsize="16" - Virindi Tank's bold column headings - is under ten points.
constexpr float kDecalFontSizeToPoints = 0.611f;

void DrawStatic(Frame& f, const ViewControl& c, const Rect& r) {
    ImFont* font = FontFor(c.bold);
    const float points = c.font_size > 0 ? static_cast<float>(c.font_size) * kDecalFontSizeToPoints : f.t.text_points;
    const float size = PointsToPixels(points) * f.s;
    const ImU32 colour = c.text_color >= 0 ? FromArgb(c.text_color) : f.t.view_text;
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);
    const ImVec4 clip(a.x, a.y, b.x, b.y);

    // A label with room for more than one line is a paragraph, and wraps; one line high is
    // a label, and is clipped rather than wrapped into the control below it.
    const bool paragraph = (b.y - a.y) > size * 1.8f;
    const ImVec2 measured = Measure(font, size, c.text);

    float x = a.x;
    if (!paragraph) {
        if (c.justify == Justify::Center) x = a.x + ((b.x - a.x) - measured.x) * 0.5f;
        else if (c.justify == Justify::Right) x = b.x - measured.x;
    }

    // VVS drew a shadow of size one as the text again, in black, a pixel down and right. A
    // label that asks for none has the theme's own, where it gives one.
    if (c.shadow)
        Text(f.draw, font, size, ImVec2(x + f.s, a.y + f.s), IM_COL32(0, 0, 0, 0xFF), c.text, &clip, paragraph ? (b.x - a.x) : 0.0f);
    HaloText(f.draw, font, size, ImVec2(x, a.y), colour, c.shadow ? kClear : f.t.text_halo, c.text, &clip, paragraph ? (b.x - a.x) : 0.0f);
}

void DrawCheckbox(Frame& f, const ViewControl& c, const Rect& r) {
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);

    // As HudCheckBox drew it: the box 13 pixels square in the control's top left corner,
    // whatever size the theme's image is, and the label 17 pixels in, centred on the box.
    // A control too small for that draws nothing, as it did there.
    if (r.h < kCheckBox || (c.text.empty() ? r.w < kCheckBox : r.w <= 19.0f)) return;
    const float box = kCheckBox * f.s;
    const ImVec2 boxA = a;

    const ImVec2 boxB(boxA.x + box, boxA.y + box);
    const Fill& look = c.checked ? f.t.check_on : f.t.check_off;
    if (!PaintOnce(f.draw, look, boxA, boxB, f.s)) {
        // A stack is drawn whole but for its tick; an image not at all.
        if (look.layers == nullptr) f.draw->AddRectFilled(boxA, boxB, Col(kStoneStandIn));
        if (c.checked) f.draw->AddCircleFilled(ImVec2(boxA.x + box * 0.5f, boxA.y + box * 0.5f), box * 0.3f, Col(IM_COL32(150, 230, 40, 255)));
    }

    ImFont* font = FontFor(false);
    const float size = PointsToPixels(f.t.text_points) * f.s;
    const ImVec4 clip(a.x, a.y, b.x, b.y);
    const ImU32 colour = c.text_color >= 0 ? FromArgb(c.text_color) : f.t.view_text;
    HaloText(f.draw, font, size, ImVec2(a.x + kCheckTextX * f.s, a.y + (box - size) * 0.5f), colour, f.t.text_halo, c.text, &clip);

    if (c.enabled && Hit("check", a, b))
        Emit(f, "set", c.name, c.checked ? "false" : "true");
}

void DrawPushButton(Frame& f, const ViewControl& c, const Rect& r) {
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);

    const bool clicked = c.enabled && Hit("push", a, b);
    const bool pressed = c.enabled && ImGui::IsItemActive() && ImGui::IsItemHovered();

    // ButtonBackground, or ButtonBackground_Down while held; then HudButton's edges, lit at the
    // top and left and shaded at the bottom and right, and the other way about while held.
    const Fill& face = pressed ? f.t.button_down : f.t.button_up;
    if (IsSet(face)) Paint(f.draw, face, a, b, f.s);
    else f.draw->AddRectFilled(a, b, Col(f.t.button_face));
    const ImU32 lit = Col(pressed ? f.t.button_shadow : f.t.button_highlight);
    const ImU32 dark = Col(pressed ? f.t.button_highlight : f.t.button_shadow);
    const float t = std::max(1.0f, f.s);
    f.draw->AddRectFilled(a, ImVec2(b.x, a.y + t), lit);
    f.draw->AddRectFilled(a, ImVec2(a.x + t, b.y), lit);
    f.draw->AddRectFilled(ImVec2(a.x, b.y - t), b, dark);
    f.draw->AddRectFilled(ImVec2(b.x - t, a.y), b, dark);

    ImFont* font = FontFor(false);
    const float size = PointsToPixels(f.t.text_points) * f.s;
    const ImVec2 measured = Measure(font, size, c.text);
    const float nudge = pressed ? t : 0.0f;
    const ImVec4 clip(a.x, a.y, b.x, b.y);
    HaloText(f.draw, font, size,
             ImVec2(a.x + ((b.x - a.x) - measured.x) * 0.5f + nudge, a.y + ((b.y - a.y) - size) * 0.5f + nudge),
             c.enabled ? f.t.button_text : IM_COL32(170, 150, 120, 255), f.t.text_halo, c.text, &clip);

    if (clicked) Emit(f, "press", c.name, std::string());
}

void DrawImageButton(Frame& f, const ViewControl& c, const Rect& r) {
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);
    const bool clicked = c.enabled && Hit("button", a, b);
    const bool pressed = c.enabled && ImGui::IsItemActive() && ImGui::IsItemHovered();
    const float nudge = pressed ? std::max(1.0f, f.s) : 0.0f;

    const Texture* tex = FindTexture(c.image);
    if (tex != nullptr) {
        DrawImage(f.draw, tex, ImVec2(a.x + nudge, a.y + nudge), ImVec2(b.x + nudge, b.y + nudge));
    } else {
        f.draw->AddRectFilled(a, b, Col(f.t.button_face));
        f.draw->AddRect(a, b, Col(f.t.button_shadow));
    }

    if (clicked) Emit(f, "press", c.name, std::string());
}

void DrawEdit(Frame& f, const ViewControl& c, const Rect& r) {
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);

    // The view's own image for the box when it names one, as Decal's imageportalsrc did;
    // otherwise the theme's: Decal's image in three pieces, Float's plain colour, Minimalist
    // Green's own ruled fill.
    const char* box_image = !c.image.empty() ? c.image.c_str() : f.t.text_box.image;
    const Texture* frame_tex = box_image != nullptr ? FindTexture(box_image) : nullptr;
    if (frame_tex != nullptr) ThreeSlice(f.draw, frame_tex, a, b, kTextBoxCap, kTextBoxCap, f.s);
    else if (f.t.text_box.layers != nullptr) DrawLayers(f.draw, f.t.text_box, a, b, f.s);
    else f.draw->AddRectFilled(a, b, Col(f.t.text_box.colour));

    // HudTextBox ruled the box as a button, lit at the top and left and shaded at the bottom
    // and right. The Decal and Float themes are drawn as they were before, without.
    if (f.t.text_box_rule) {
        const float t = std::max(1.0f, f.s);
        f.draw->AddRectFilled(a, ImVec2(a.x + t, b.y), Col(f.t.button_highlight));
        f.draw->AddRectFilled(a, ImVec2(b.x, a.y + t), Col(f.t.button_highlight));
        f.draw->AddRectFilled(ImVec2(a.x, b.y - t), b, Col(f.t.button_shadow));
        f.draw->AddRectFilled(ImVec2(b.x - t, a.y), b, Col(f.t.button_shadow));
    }

    ImFont* font = FontFor(false);
    const float size = std::min(PointsToPixels(f.t.text_box_points), r.h) * f.s;

    const std::string key = MemoryKey(c.name);
    auto found = f.memory.edits.find(key);
    const bool editing = found != f.memory.edits.end();

    // The text box shows the plugin's value except while the player is typing in it, when
    // it shows theirs; the plugin hears the new text once, when they let go.
    std::string scratch;
    if (!editing) scratch = c.value;
    std::string& text = editing ? found->second.text : scratch;

    ImGui::PushStyleColor(ImGuiCol_FrameBg, IM_COL32(0, 0, 0, 0));
    ImGui::PushStyleColor(ImGuiCol_FrameBgHovered, IM_COL32(0, 0, 0, 0));
    ImGui::PushStyleColor(ImGuiCol_FrameBgActive, IM_COL32(0, 0, 0, 0));
    ImGui::PushStyleColor(ImGuiCol_Text, f.t.text_box_text);
    ImGui::PushStyleColor(ImGuiCol_TextSelectedBg, IM_COL32(120, 90, 40, 160));
    ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(3.0f * f.s, std::max(0.0f, ((b.y - a.y) - size) * 0.5f)));
    ImGui::PushStyleVar(ImGuiStyleVar_FrameBorderSize, 0.0f);
    ImGui::PushFont(font, size);

    ImGui::SetCursorScreenPos(a);
    ImGui::SetNextItemWidth(b.x - a.x);
    if (!c.enabled) ImGui::BeginDisabled();

    auto resize = [](ImGuiInputTextCallbackData* data) -> int {
        if (data->EventFlag == ImGuiInputTextFlags_CallbackResize) {
            auto* str = static_cast<std::string*>(data->UserData);
            str->resize(static_cast<size_t>(data->BufTextLen));
            data->Buf = str->data();
        }
        return 0;
    };
    if (b.x - a.x >= 1.0f && b.y - a.y >= 1.0f)
        ImGui::InputText("##edit", text.data(), text.capacity() + 1, ImGuiInputTextFlags_CallbackResize, resize, &text);

    if (!c.enabled) ImGui::EndDisabled();
    ImGui::PopFont();
    ImGui::PopStyleVar(2);
    ImGui::PopStyleColor(5);

    if (ImGui::IsItemActive()) {
        EditMemory& edit = f.memory.edits[key];
        if (!editing) edit.text = text;
        edit.frame = ImGui::GetFrameCount();
    } else if (editing) {
        if (ImGui::IsItemDeactivatedAfterEdit()) Emit(f, "set", c.name, found->second.text);
        f.memory.edits.erase(found);
    }
}

void DrawChoice(Frame& f, const ViewControl& c, const Rect& r) {
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);

    // The arrow keeps its proportions at the control's height, at the right-hand end; one the
    // theme builds for itself is square. A theme with a ComboBackground_Unselected of its own
    // has it stop at the arrow, as HudCombo did; the rest lie their list's under both.
    const Texture* arrow = f.t.combo_arrow.layers == nullptr ? FindTexture(f.t.combo_arrow.image) : nullptr;
    const float ah = b.y - a.y;
    const float aw = arrow != nullptr ? ah * arrow->width / arrow->height : ah;
    if (IsSet(f.t.combo_box)) Paint(f.draw, f.t.combo_box, a, ImVec2(b.x - aw, b.y), f.s);
    else Paint(f.draw, f.t.list, a, b, f.s);
    if (f.t.combo_arrow.layers != nullptr) DrawLayers(f.draw, f.t.combo_arrow, ImVec2(b.x - aw, a.y), b, f.s);
    else DrawImage(f.draw, arrow, ImVec2(b.x - aw, a.y), b);

    ImFont* font = FontFor(false);
    const float size = PointsToPixels(f.t.text_points) * f.s;
    const ImVec4 clip(a.x, a.y, b.x - aw, b.y);
    const std::string& shown = c.selected >= 0 && c.selected < static_cast<int>(c.options.size())
                                   ? c.options[static_cast<size_t>(c.selected)] : c.value;
    HaloText(f.draw, font, size, ImVec2(a.x + 3.0f * f.s, a.y + ((b.y - a.y) - size) * 0.5f), f.t.list_text, f.t.text_halo, shown, &clip);

    if (c.enabled && Hit("choice", a, b)) ImGui::OpenPopup("options");

    // The list drops below the control, as wide as it, and as tall as eight options before
    // it scrolls.
    const float row = kRowHeight * f.s;
    const size_t count = c.options.size();
    const float height = row * static_cast<float>(std::min<size_t>(std::max<size_t>(count, 1), 8));
    ImGui::SetNextWindowPos(ImVec2(a.x, b.y));
    ImGui::SetNextWindowSize(ImVec2(b.x - a.x, height));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0, 0));
    ImGui::PushStyleVar(ImGuiStyleVar_PopupBorderSize, 1.0f);
    ImGui::PushStyleColor(ImGuiCol_Border, f.t.button_shadow);
    ImGui::PushStyleColor(ImGuiCol_PopupBg, IM_COL32(0, 0, 0, 0));
    if (ImGui::BeginPopup("options", ImGuiWindowFlags_NoMove)) {
        ImDrawList* draw = ImGui::GetWindowDrawList();
        const ImVec2 pa = ImGui::GetWindowPos();
        const float inner_w = ImGui::GetContentRegionAvail().x;
        Paint(draw, Either(f.t.combo_list, f.t.list), pa, ImVec2(pa.x + ImGui::GetWindowWidth(), pa.y + ImGui::GetWindowHeight()), f.s);

        for (size_t i = 0; i < count; ++i) {
            // Window-relative, so ImGui's own scrolling moves the rows when there are more
            // than fit.
            ImGui::SetCursorPos(ImVec2(0.0f, row * static_cast<float>(i)));
            const ImVec2 ra = ImGui::GetCursorScreenPos();
            const ImVec2 rb(ra.x + std::max(1.0f, inner_w), ra.y + row);
            ImGui::PushID(static_cast<int>(i));
            const bool picked = ImGui::InvisibleButton("option", ImVec2(rb.x - ra.x, row));
            const bool hovered = ImGui::IsItemHovered();
            ImGui::PopID();

            if (static_cast<int>(i) == c.selected) Paint(draw, f.t.combo_selected, ra, rb, f.s);
            else if (hovered) Paint(draw, f.t.combo_selecting, ra, rb, f.s);

            HaloText(draw, font, size, ImVec2(ra.x + 3.0f * f.s, ra.y + (row - size) * 0.5f), f.t.list_text, f.t.text_halo, c.options[i]);

            if (picked) {
                if (static_cast<int>(i) != c.selected) Emit(f, "set", c.name, std::to_string(i));
                ImGui::CloseCurrentPopup();
            }
        }

        ImGui::EndPopup();
    }
    ImGui::PopStyleColor(2);
    ImGui::PopStyleVar(2);
}

void DrawSlider(Frame& f, const ViewControl& c, const Rect& r) {
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);
    const bool usable = std::isfinite(c.min) && std::isfinite(c.max) && c.max > c.min;

    const std::string key = MemoryKey(c.name);
    auto found = f.memory.edits.find(key);
    const bool editing = found != f.memory.edits.end();
    double value = editing ? found->second.number : std::clamp(ParseNumber(c.value, c.min), c.min, usable ? c.max : c.min);

    const float nub_w = kNubWidth * f.s;
    const float nub_h = kNubHeight * f.s;

    // The track: a band of the inner colour edged in the outer, across the middle -
    // SliderBarInner and SliderBarOuter.
    if (!c.vertical) {
        const float cy = (a.y + b.y) * 0.5f;
        const float half = std::max(1.5f, 2.0f * f.s);
        f.draw->AddRectFilled(ImVec2(a.x, cy - half), ImVec2(b.x, cy + half), Col(f.t.slider_inner));
        f.draw->AddRect(ImVec2(a.x, cy - half), ImVec2(b.x, cy + half), Col(f.t.slider_outer));
    } else {
        const float cx = (a.x + b.x) * 0.5f;
        const float half = std::max(1.5f, 2.0f * f.s);
        f.draw->AddRectFilled(ImVec2(cx - half, a.y), ImVec2(cx + half, b.y), Col(f.t.slider_inner));
        f.draw->AddRect(ImVec2(cx - half, a.y), ImVec2(cx + half, b.y), Col(f.t.slider_outer));
    }

    const bool live = c.enabled && usable;
    if (live) {
        Hit("slider", a, b);
        if (ImGui::IsItemActive()) {
            const ImVec2 mouse = ImGui::GetIO().MousePos;
            double t = !c.vertical ? (mouse.x - a.x - nub_w * 0.5f) / std::max(1.0f, (b.x - a.x) - nub_w)
                                   : 1.0 - (mouse.y - a.y - nub_w * 0.5f) / std::max(1.0f, (b.y - a.y) - nub_w);
            t = std::clamp(t, 0.0, 1.0);

            // Decal's sliders stop at whole numbers.
            value = std::round(c.min + t * (c.max - c.min));
            EditMemory& edit = f.memory.edits[key];
            edit.number = value;
            edit.frame = ImGui::GetFrameCount();
        } else if (editing) {
            if (ImGui::IsItemDeactivated()) Emit(f, "set", c.name, FormatInteger(found->second.number));
            f.memory.edits.erase(key);
        }
    }

    const double t = usable ? (value - c.min) / (c.max - c.min) : 0.0;
    ImVec2 na;
    ImVec2 nb;
    if (!c.vertical) {
        const float x = a.x + static_cast<float>(t) * ((b.x - a.x) - nub_w);
        na = ImVec2(x, (a.y + b.y - nub_h) * 0.5f);
        nb = ImVec2(x + nub_w, na.y + nub_h);
    } else {
        const float y = b.y - nub_w - static_cast<float>(t) * ((b.y - a.y) - nub_w);
        na = ImVec2((a.x + b.x - nub_h) * 0.5f, y);
        nb = ImVec2(na.x + nub_h, y + nub_w);
    }
    if (!PaintOnce(f.draw, f.t.slider_nub, na, nb, f.s) && f.t.slider_nub.layers == nullptr)
        f.draw->AddRectFilled(na, nb, Col(f.t.button_face));
}

void DrawProgress(Frame& f, const ViewControl& c, const Rect& r) {
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);
    const double value = ParseNumber(c.value, c.min);
    const double t = c.max > c.min ? std::clamp((value - c.min) / (c.max - c.min), 0.0, 1.0) : 0.0;
    f.draw->AddRectFilled(a, b, Col(f.t.progress_empty));
    f.draw->AddRectFilled(a, ImVec2(a.x + static_cast<float>(t) * (b.x - a.x), b.y), Col(f.t.progress_filled));
}

// The client's own scrollbar: arrow buttons at each end, the track tiled between, and the
// blue bubble in three pieces - or whatever the theme builds them of. As wide as a button is
// high. Returns the new scroll offset in rows.
float DrawScrollbar(Frame& f, ImVec2 a, ImVec2 b, float offset, float visible_rows, float total_rows) {
    const float button = f.t.scroll_button * f.s;
    const float max_offset = std::max(0.0f, total_rows - visible_rows);

    PaintOnce(f.draw, f.t.scroll_up, a, ImVec2(b.x, a.y + button), f.s);
    PaintOnce(f.draw, f.t.scroll_down, ImVec2(a.x, b.y - button), b, f.s);
    if (Hit("up", a, ImVec2(b.x, a.y + button))) offset -= 1.0f;
    if (Hit("down", ImVec2(a.x, b.y - button), b)) offset += 1.0f;

    const ImVec2 ta(a.x, a.y + button);
    const ImVec2 tb(b.x, b.y - button);
    ImGui::PushClipRect(ta, tb, true);
    {
        // The track image is narrower than the bar; stretched across, tiled down.
        const Texture* track = f.t.scroll_track.image != nullptr ? FindTexture(f.t.scroll_track.image) : nullptr;
        if (track != nullptr) {
            const float th = track->height * f.s;
            for (float y = ta.y; y < tb.y; y += th) {
                const float h = std::min(th, tb.y - y);
                DrawImage(f.draw, track, ImVec2(ta.x, y), ImVec2(tb.x, y + h), ImVec2(0, 0), ImVec2(1, h / th));
            }
        } else if (f.t.scroll_track.layers != nullptr) {
            DrawLayers(f.draw, f.t.scroll_track, ta, tb, f.s);
        } else {
            f.draw->AddRectFilled(ta, tb, Col(f.t.scroll_track.colour));
        }
    }
    ImGui::PopClipRect();

    const float track_h = tb.y - ta.y;
    if (total_rows > visible_rows && track_h > 1.0f) {
        const float bubble_h = std::max(kBubbleMin * f.s, track_h * visible_rows / total_rows);
        const float bubble_y = ta.y + (track_h - bubble_h) * (max_offset > 0.0f ? offset / max_offset : 0.0f);
        const ImVec2 ba(ta.x, bubble_y);
        const ImVec2 bb(tb.x, bubble_y + bubble_h);

        Hit("bubble", ba, bb);
        const bool dragging = ImGui::IsItemActive();
        const bool hovered = ImGui::IsItemHovered();
        if (dragging && track_h > bubble_h)
            offset += ImGui::GetIO().MouseDelta.y / (track_h - bubble_h) * max_offset;

        const float cap = f.t.bubble_cap * f.s;
        PaintOnce(f.draw, f.t.bubble_top, ba, ImVec2(bb.x, ba.y + cap), f.s);
        PaintOnce(f.draw, dragging ? f.t.bubble_drag : hovered ? f.t.bubble_hover : f.t.bubble_middle, ImVec2(ba.x, ba.y + cap),
                  ImVec2(bb.x, bb.y - cap), f.s);
        PaintOnce(f.draw, f.t.bubble_bottom, ImVec2(ba.x, bb.y - cap), bb, f.s);
    }

    return std::clamp(offset, 0.0f, max_offset);
}

void DrawList(Frame& f, const ViewControl& c, const Rect& r) {
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);

    Paint(f.draw, f.t.list, a, b, f.s);

    const float row = kRowHeight * f.s;
    const float visible = (b.y - a.y) / row;
    const float total = static_cast<float>(c.rows.size());
    const bool scrolling = total > visible;
    const float bar = scrolling ? f.t.scroll_button * f.s : 0.0f;

    float& offset = f.memory.scroll[MemoryKey(c.name)];

    // Fixed columns take their width; the rest share what is left, as Decal's list did.
    float fixed = 0.0f;
    int sharing = 0;
    for (const ViewColumn& column : c.columns) {
        if (column.width > 0) fixed += (static_cast<float>(column.width) + kColumnPadding) * f.s;
        else ++sharing;
    }
    const float spare = std::max(0.0f, (b.x - a.x - bar) - fixed);
    const float shared = sharing > 0 ? spare / static_cast<float>(sharing) : 0.0f;

    ImFont* font = FontFor(false);
    const float size = PointsToPixels(f.t.text_points) * f.s;
    const ImVec2 inner_b(b.x - bar, b.y);

    // The wheel scrolls whatever list is under the pointer, three rows a notch.
    if (ImGui::IsMouseHoveringRect(a, b) && ImGui::IsWindowHovered())
        offset -= ImGui::GetIO().MouseWheel * 3.0f;

    const int first = static_cast<int>(std::floor(std::max(0.0f, offset)));
    const float shift = (offset - static_cast<float>(first)) * row;

    ImGui::PushClipRect(a, inner_b, true);
    for (int i = first; i < static_cast<int>(c.rows.size()); ++i) {
        const float y = a.y + static_cast<float>(i - first) * row - shift;
        if (y >= b.y) break;

        const ViewRow& data = c.rows[static_cast<size_t>(i)];
        float x = a.x;
        for (size_t col = 0; col < c.columns.size(); ++col) {
            const ViewColumn& column = c.columns[col];
            const float w = column.width > 0 ? (static_cast<float>(column.width) + kColumnPadding) * f.s : shared;
            const ImVec2 ca(x, y);
            const ImVec2 cb(std::min(x + w, inner_b.x), y + row);
            x += w;
            if (cb.x <= ca.x) continue;

            const ViewCell* cell = col < data.cells.size() ? &data.cells[col] : nullptr;

            if (cell != nullptr) {
                if (column.type == ViewColumnType::Check) {
                    // A checkbox built of layers is drawn at the 13 pixels VVS laid them out for.
                    const Fill& look = cell->checked ? f.t.check_on : f.t.check_off;
                    const float box = look.layers != nullptr ? std::min(cb.x - ca.x, kCheckBox * f.s) : std::min(cb.x - ca.x, row) - 2.0f * f.s;
                    const ImVec2 ba(ca.x + ((cb.x - ca.x) - box) * 0.5f, ca.y + (row - box) * 0.5f);
                    PaintOnce(f.draw, look, ba, ImVec2(ba.x + box, ba.y + box), f.s);
                } else if (column.type == ViewColumnType::Icon) {
                    const float box = std::min(cb.x - ca.x, row);
                    const ImVec2 ba(ca.x + ((cb.x - ca.x) - box) * 0.5f, ca.y + (row - box) * 0.5f);
                    DrawImage(f.draw, FindTexture(cell->image), ba, ImVec2(ba.x + box, ba.y + box));
                } else {
                    const ImVec4 clip(ca.x, ca.y, cb.x - 1.0f, cb.y);
                    HaloText(f.draw, font, size, ImVec2(ca.x + 2.0f * f.s, ca.y + (row - size) * 0.5f),
                             cell->color >= 0 ? FromArgb(cell->color) : f.t.list_text, f.t.text_halo, cell->text, &clip);
                }
            }

            ImGui::PushID(i);
            ImGui::PushID(static_cast<int>(col));
            if (c.enabled && Hit("cell", ca, cb))
                Emit(f, "click", c.name, std::to_string(col), std::to_string(i));
            ImGui::PopID();
            ImGui::PopID();
        }
    }
    ImGui::PopClipRect();

    if (scrolling) offset = DrawScrollbar(f, ImVec2(b.x - bar, a.y), b, offset, visible, total);
    else offset = 0.0f;
}

void DrawNotebook(Frame& f, const ViewControl& c, const Rect& r) {
    if (c.pages.empty()) return;

    NotebookMemory& memory = f.memory.notebooks[MemoryKey(c.name)];

    // The page the player chose is theirs to keep across publishes; a page the plugin
    // chooses - a different number than it said last time - wins.
    if (c.selected != memory.last_published) {
        if (c.selected >= 0) memory.active = c.selected;
        memory.last_published = c.selected;
    }
    memory.active = std::clamp(memory.active, 0, static_cast<int>(c.pages.size()) - 1);

    ImFont* font = FontFor(false);
    const float size = PointsToPixels(f.t.tab_points) * f.s;
    const float tab_h = f.t.tab_height * f.s;
    const ImVec2 a = Screen(f, r.x, r.y);
    const ImVec2 b = Screen(f, r.x + r.w, r.y + r.h);
    const bool images = f.t.tabs == TabStyle::Images;

    float x = a.x;
    ImGui::PushClipRect(a, ImVec2(b.x, a.y + tab_h), true);
    for (size_t i = 0; i < c.pages.size(); ++i) {
        const bool active = static_cast<int>(i) == memory.active;
        const std::string& label = c.pages[i].label;
        const float text_w = Measure(font, size, label).x;

        // The Minimalist themes' tabs are as HudTabView measured them: the label, or the two
        // ends if they are wider, and TabTextHPadding once, the label centred. The Decal and
        // Float tabs keep the wider measure they were drawn at before.
        const float ends = f.t.tab_cap + f.t.tab_cap_right;
        const float w = f.t.tabs == TabStyle::Fills ? std::max(text_w, ends * f.s) + f.t.tab_padding * f.s
                                                    : (ends + f.t.tab_padding * 2.0f) * f.s + text_w;
        const ImVec2 ta(x, a.y);
        const ImVec2 tb(x + w, a.y + tab_h);

        const Texture* left = images ? FindTexture(active ? kTabActiveLeft : kTabIdleLeft) : nullptr;
        const Texture* centre = images ? FindTexture(active ? kTabActiveCenter : kTabIdleCenter) : nullptr;
        const Texture* right = images ? FindTexture(active ? kTabActiveRight : kTabIdleRight) : nullptr;
        if (f.t.tabs == TabStyle::Colour) {
            f.draw->AddRectFilled(ta, tb, Col(f.t.tab_colour));
        } else if (f.t.tabs == TabStyle::Fills) {
            // TabViewSelected_Left, _Center and _Right, or TabViewNormal_*, each filled once.
            const Fill* pieces = active ? f.t.tab_selected : f.t.tab_idle;
            const float l = f.t.tab_cap * f.s;
            const float rr = f.t.tab_cap_right * f.s;
            ImGui::PushClipRect(ta, tb, true);
            PaintOnce(f.draw, pieces[0], ta, ImVec2(ta.x + l, tb.y), f.s);
            PaintOnce(f.draw, pieces[1], ImVec2(ta.x + l, ta.y), ImVec2(tb.x - rr, tb.y), f.s);
            PaintOnce(f.draw, pieces[2], ImVec2(tb.x - rr, ta.y), tb, f.s);
            ImGui::PopClipRect();
        } else if (left != nullptr && centre != nullptr && right != nullptr) {
            const float cap = f.t.tab_cap * f.s;
            DrawImage(f.draw, left, ta, ImVec2(ta.x + cap, tb.y));
            DrawImage(f.draw, centre, ImVec2(ta.x + cap, ta.y), ImVec2(tb.x - cap, tb.y));
            DrawImage(f.draw, right, ImVec2(tb.x - cap, ta.y), tb);
        } else {
            f.draw->AddRectFilled(ta, tb, Col(active ? IM_COL32(0xB4, 0x7E, 0x2B, 255) : IM_COL32(0x85, 0x4C, 0x12, 255)));
        }

        const float text_x = f.t.tabs == TabStyle::Fills ? ta.x + std::floor((w - text_w) * 0.5f) : ta.x + (f.t.tab_cap + f.t.tab_padding) * f.s;
        HaloText(f.draw, font, size, ImVec2(text_x, ta.y + (tab_h - size) * 0.5f), active ? f.t.tab_text_selected : f.t.tab_text_idle,
                 active ? f.t.tab_halo_selected : f.t.tab_halo_idle, label);

        ImGui::PushID(static_cast<int>(i));
        if (Hit("tab", ta, tb) && !active) {
            memory.active = static_cast<int>(i);
            Emit(f, "page", c.name, std::to_string(i));
        }
        ImGui::PopID();

        x += w;
    }

    // Beyond the last tab: TabViewExtraSpace, which the Decal theme leaves to the
    // background under it and Float fills like a tab. HudTabView never drew it, and the
    // Minimalist themes leave it to the background as well.
    if (f.t.tabs == TabStyle::Colour && x < b.x) f.draw->AddRectFilled(ImVec2(x, a.y), ImVec2(b.x, a.y + tab_h), Col(f.t.tab_colour));
    ImGui::PopClipRect();

    const ViewPage& page = c.pages[static_cast<size_t>(memory.active)];
    if (page.content.empty()) return;

    const Rect area{r.x, r.y + f.t.tab_height, r.w, std::max(0.0f, r.h - f.t.tab_height)};
    ImGui::PushID(memory.active);
    ImGui::PushClipRect(Screen(f, area.x, area.y), Screen(f, area.x + area.w, area.y + area.h), true);
    DrawControl(f, page.content[0], area, 0);
    ImGui::PopClipRect();
    ImGui::PopID();
}

void DrawControl(Frame& f, const ViewControl& c, const Rect& parent, int ordinal) {
    if (!c.visible) return;

    const Rect r = Place(c, parent);

    // Named controls are identified by name, so a list that grows does not move the edit
    // box's identity under the player's cursor; the unnamed ones - layouts - by position.
    if (c.name.empty()) ImGui::PushID(ordinal);
    else ImGui::PushID(c.name.c_str());

    const bool faded = !c.enabled;
    if (faded) ImGui::PushStyleVar(ImGuiStyleVar_Alpha, ImGui::GetStyle().Alpha * 0.5f);

    // Each layout paints the theme's background over itself before its controls, as VVS's
    // HudFixedLayout and HudTabView did - so a tiled background starts afresh at each one,
    // and it is under every control whichever layout the control is in.
    if (c.type == ViewControlType::Fixed || c.type == ViewControlType::Notebook)
        Paint(f.draw, f.t.view, Screen(f, r.x, r.y), Screen(f, r.x + r.w, r.y + r.h), f.s);

    switch (c.type) {
        case ViewControlType::Fixed: {
            int child_ordinal = 0;
            for (const ViewControl& child : c.children)
                DrawControl(f, child, r, child_ordinal++);
            break;
        }
        case ViewControlType::Notebook: DrawNotebook(f, c, r); break;
        case ViewControlType::Static: DrawStatic(f, c, r); break;
        case ViewControlType::Checkbox: DrawCheckbox(f, c, r); break;
        case ViewControlType::PushButton: DrawPushButton(f, c, r); break;
        case ViewControlType::Button: DrawImageButton(f, c, r); break;
        case ViewControlType::Edit: DrawEdit(f, c, r); break;
        case ViewControlType::Choice: DrawChoice(f, c, r); break;
        case ViewControlType::Slider: DrawSlider(f, c, r); break;
        case ViewControlType::List: DrawList(f, c, r); break;
        case ViewControlType::Progress: DrawProgress(f, c, r); break;
        case ViewControlType::Unknown: break;
    }

    if (faded) ImGui::PopStyleVar();
    ImGui::PopID();
}

// A title-bar button as VVS's FillSingle drew it, stretched to the button: the theme's look
// for it - the close button's image on a square of black, Minimalist Black's glyph in a frame -
// or a plugin's own image; the held look while held. True when clicked.
void ThemedTooltip(const Theme& t, const char* text);

bool DrawTitleButton(const char* id, const Face& face, ImVec2 a, float size, const char* tooltip, const Theme& t) {
    const ImVec2 b(a.x + size, a.y + size);
    const bool clicked = Hit(id, a, b);
    const bool held = ImGui::IsItemActive() && ImGui::IsItemHovered();
    if (!held) ThemedTooltip(t, tooltip);
    ImDrawList* draw = ImGui::GetWindowDrawList();
    if (!PaintOnce(draw, held ? face.down : face.up, a, b, kScale))
        draw->AddRect(a, b, Col(IM_COL32(0x46, 0x28, 0x0F, 0xFF)));
    return clicked;
}

// One edge of Float's frame: black over the half nearer the window, then the rule image
// tiled along the whole edge over it - which leaves the outer pixel clear, as VVS's did.
void FloatEdge(ImDrawList* draw, ImVec2 a, ImVec2 b, bool across, bool black_second_half, float s) {
    if (b.x <= a.x || b.y <= a.y) return;
    if (across) {
        const float mid = std::floor((a.y + b.y) * 0.5f);
        if (black_second_half) draw->AddRectFilled(ImVec2(a.x, mid), b, Col(IM_COL32(0, 0, 0, 0xFF)));
        else draw->AddRectFilled(a, ImVec2(b.x, mid), Col(IM_COL32(0, 0, 0, 0xFF)));
    } else {
        const float mid = std::floor((a.x + b.x) * 0.5f);
        if (black_second_half) draw->AddRectFilled(ImVec2(mid, a.y), b, Col(IM_COL32(0, 0, 0, 0xFF)));
        else draw->AddRectFilled(a, ImVec2(mid, b.y), Col(IM_COL32(0, 0, 0, 0xFF)));
    }
    ImGui::PushClipRect(a, b, true);
    Tile(draw, across ? kFloatRuleAcross : kFloatRuleDown, a, b, s, IM_COL32(144, 120, 84, 0xFF));
    ImGui::PopClipRect();
}

// The window's frame in its theme: Decal's a solid dark line all round, Float's a thin gold
// rule on black with a square at each corner.
void DrawWindowFrame(ImDrawList* draw, const Theme& t, ImVec2 wa, ImVec2 wb, float border, float s) {
    if (border <= 0.0f) return;
    const float e = border * s;
    if (t.frame == FrameStyle::Solid) {
        draw->AddRectFilled(wa, ImVec2(wb.x, wa.y + e), Col(t.frame_colour));
        draw->AddRectFilled(ImVec2(wa.x, wb.y - e), wb, Col(t.frame_colour));
        draw->AddRectFilled(ImVec2(wa.x, wa.y + e), ImVec2(wa.x + e, wb.y - e), Col(t.frame_colour));
        draw->AddRectFilled(ImVec2(wb.x - e, wa.y + e), ImVec2(wb.x, wb.y - e), Col(t.frame_colour));
        return;
    }

    FloatEdge(draw, ImVec2(wa.x + e, wa.y), ImVec2(wb.x - e, wa.y + e), true, true, s);      // top
    FloatEdge(draw, ImVec2(wa.x + e, wb.y - e), ImVec2(wb.x - e, wb.y), true, false, s);     // bottom
    FloatEdge(draw, ImVec2(wa.x, wa.y + e), ImVec2(wa.x + e, wb.y - e), false, true, s);     // left
    FloatEdge(draw, ImVec2(wb.x - e, wa.y + e), ImVec2(wb.x, wb.y - e), false, false, s);    // right
    const ImVec2 corners[] = {wa, ImVec2(wb.x - e, wa.y), ImVec2(wa.x, wb.y - e), ImVec2(wb.x - e, wb.y - e)};
    for (const ImVec2& c : corners)
        Tile(draw, kFloatCorner, c, ImVec2(c.x + e, c.y + e), s, IM_COL32(163, 132, 85, 0xFF));
}

// VVS's primary theme, which every VVS window with no theme of its own is drawn in: what
// the player last picked with the VVS bar's "ab" square, kept in the ini under the owner "*",
// else VVS's own default as the host read it from the registry, else Decal.
std::string g_host_default_theme;
constexpr const char* kGlobalThemeOwner = "*";

const Theme& GlobalTheme() {
    auto found = Memory().find(kGlobalThemeOwner);
    if (found != Memory().end() && !found->second.theme.empty()) return ThemeNamed(found->second.theme);
    if (!g_host_default_theme.empty()) return ThemeNamed(g_host_default_theme);
    return DefaultTheme();
}

// VVS's bar is itself a VVS window, "VVS Bar", with a theme of its own the player may pick from
// its coral box; until they do, VVS's primary theme.
constexpr const char* kVvsBarOwner = "VirindiViewService:VVS Bar";

const Theme& BarTheme() {
    auto found = Memory().find(kVvsBarOwner);
    if (found != Memory().end() && !found->second.theme.empty()) return ThemeNamed(found->second.theme);
    return GlobalTheme();
}

// The colour a theme's tooltips and menus are edged in: its TooltipBorder*, which is Float's
// gold, Minimalist Green's tan, and every other theme's button shadow.
ImU32 TooltipBorder(const Theme& t) {
    if (t.tooltip_border != kClear) return t.tooltip_border;
    return t.frame == FrameStyle::Float ? IM_COL32(0xEF, 0xA5, 0x10, 0xFF) : t.button_shadow;
}

// Inside a popup that is begun, over its background and under its contents: a surface of the
// theme's that a plain colour will not do for - Minimalist Green's stone - inside the popup's
// one-pixel border.
void PaintPopupBack(const Fill& fill) {
    if (fill.image == nullptr && fill.layers == nullptr) return;
    const ImVec2 at = ImGui::GetWindowPos();
    const ImVec2 size = ImGui::GetWindowSize();
    Paint(ImGui::GetWindowDrawList(), fill, ImVec2(at.x + 1.0f, at.y + 1.0f), ImVec2(at.x + size.x - 1.0f, at.y + size.y - 1.0f), kScale);
}

// A tooltip as the theme drew one: a one-pixel border, two pixels of padding, and the view's
// own background, text colour and font shadow.
void ThemedTooltip(const Theme& t, const char* text) {
    if (text == nullptr || text[0] == '\0' || !ImGui::IsItemHovered(ImGuiHoveredFlags_ForTooltip)) return;

    // The Decal theme's parchment is only ever stood in for, as it was before the others; the
    // Minimalist themes' surfaces are painted as they are.
    const bool painted = IsSet(t.menu_back) && t.view.image != nullptr;
    ImGui::PushStyleColor(ImGuiCol_PopupBg, painted ? kClear : t.view.colour);
    ImGui::PushStyleColor(ImGuiCol_Border, TooltipBorder(t));
    ImGui::PushStyleColor(ImGuiCol_Text, t.view_text);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(2.0f, 2.0f));
    ImGui::PushStyleVar(ImGuiStyleVar_PopupBorderSize, 1.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 1.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_PopupRounding, 0.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 0.0f);
    const float size = PointsToPixels(t.text_points);
    ImGui::PushFont(FontFor(false), size);
    if (ImGui::BeginTooltip()) {
        if (painted) PaintPopupBack(t.view);
        if (t.text_halo == kClear) {
            ImGui::TextUnformatted(text);
        } else {
            const ImVec2 at = ImGui::GetCursorScreenPos();
            ImGui::Dummy(ImGui::CalcTextSize(text));
            HaloText(ImGui::GetWindowDrawList(), FontFor(false), size, at, t.view_text, t.text_halo, text);
        }
        ImGui::EndTooltip();
    }
    ImGui::PopFont();
    ImGui::PopStyleVar(5);
    ImGui::PopStyleColor(3);
}

// Where a hudified window may be: its body - what shows of it - on screen, and against any
// edge it was pushed to, as VVS's GhostSticky kept it there as the window or the screen
// changed size. With `note`, the edges it touches now are remembered.
ImVec2 KeepOnScreen(WindowMemory& memory, ImVec2 at, ImVec2 size, float border, float head, bool note = false) {
    const ImGuiViewport* viewport = ImGui::GetMainViewport();
    const ImVec2 lo(viewport->WorkPos.x - border, viewport->WorkPos.y - border - head);
    const ImVec2 hi(viewport->WorkPos.x + viewport->WorkSize.x - size.x + border,
                    viewport->WorkPos.y + viewport->WorkSize.y - size.y + border);
    ImVec2 kept(std::clamp(at.x, lo.x, std::max(lo.x, hi.x)), std::clamp(at.y, lo.y, std::max(lo.y, hi.y)));

    if (note) {
        if (kept.x <= lo.x) memory.stuck += "L";
        else if (kept.x >= hi.x) memory.stuck += "R";
        if (kept.y <= lo.y) memory.stuck += "T";
        else if (kept.y >= hi.y) memory.stuck += "B";
        ImGui::MarkIniSettingsDirty();
    }

    if (memory.stuck.find('L') != std::string::npos) kept.x = lo.x;
    if (memory.stuck.find('R') != std::string::npos) kept.x = std::max(lo.x, hi.x);
    if (memory.stuck.find('T') != std::string::npos) kept.y = lo.y;
    if (memory.stuck.find('B') != std::string::npos) kept.y = std::max(lo.y, hi.y);
    if (kept.x != at.x || kept.y != at.y) ImGui::SetWindowPos(kept);
    return kept;
}

// What the player has chosen for a window, or failing that what the host says it starts as,
// or for a VVS window with no theme of its own, VVS's primary theme.
const Theme& ThemeOf(const WindowMemory& memory, const View& view) {
    if (!memory.theme.empty()) return ThemeNamed(memory.theme);
    if (!view.theme.empty()) return ThemeNamed(view.theme);
    if (view.bar == "vvs") return GlobalTheme();
    return DefaultTheme();
}

bool GhostedOf(const WindowMemory& memory, const View& view) {
    return view.ghostable && memory.ghosted.value_or(view.ghosted);
}

bool ClickThroughOf(const WindowMemory& memory, const View& view) {
    return view.click_throughable && memory.click_through.value_or(view.click_through);
}

// The first time in a session a window is hudified, VVS said how to get at it again - in the
// game's chat, which only the host can write to.
void SayHudifiedOnce(const std::string& title, std::vector<Command>& commands) {
    static bool said = false;
    if (said) return;
    said = true;
    const std::string rule(59, '*');
    for (const std::string& line : {rule,
                                    "The window \"" + title + "\" has been hudified. To access the window controls, hold down the left Ctrl key. The window can now be pinned to the sides of the screen.",
                                    rule}) {
        Command say;
        say.name = "say";
        say.value = line;
        commands.push_back(std::move(say));
    }
}

// A press on one of the plugin's own title-bar buttons, sent as a push button's would be.
void PressTitleButton(const std::string& owner, const std::string& name, std::vector<Command>& commands) {
    Command command;
    command.name = "press";
    command.owner = owner;
    command.control_id = name;
    commands.push_back(std::move(command));
}

// "color:AARRGGBB": an image that is only a colour, as VVS's ACImage(Color) was - how Virindi
// HUDs gave its windows their icons.
bool ColourKey(const std::string& key, ImU32& colour) {
    constexpr std::string_view prefix = "color:";
    if (key.size() != prefix.size() + 8 || _strnicmp(key.c_str(), prefix.data(), prefix.size()) != 0) return false;
    uint32_t argb = 0;
    const char* digits = key.c_str() + prefix.size();
    const auto result = std::from_chars(digits, digits + 8, argb, 16);
    if (result.ec != std::errc() || result.ptr != digits + 8) return false;
    colour = FromArgb(static_cast<int64_t>(argb));
    return true;
}

// A window menu in its theme. The Decal and Float themes' are drawn as they were before: in
// their lists' colours, at ImGui's own spacing. The others' as VVS drew its context menus:
// on the view's background edged like a tooltip, items sixteen pixels high two pixels in from
// the edge, each lettered after a sixteen-pixel strip of MenuLeftAreaFill down the left, in the
// tooltip's colour and font shadow, and the one under the pointer on ComboBackground_Selected.
constexpr float kMenuItemHeight = 16.0f;     // MenuItemHeight
constexpr float kMenuLeftArea = 16.0f;       // MenuLeftAreaWidth
constexpr float kMenuInset = 3.0f;           // TooltipBorder_Size and TooltipPadding

struct MenuLook {
    int colours = 0;
    int vars = 0;
};

MenuLook PushMenuLook(const Theme& t) {
    MenuLook look;
    if (!IsSet(t.menu_back)) {
        ImGui::PushStyleColor(ImGuiCol_PopupBg, t.list.image == nullptr ? t.list.colour : kStoneStandIn);
        ImGui::PushStyleColor(ImGuiCol_Text, t.list_text);
        ImGui::PushStyleColor(ImGuiCol_Border, t.button_shadow);
        ImGui::PushStyleColor(ImGuiCol_HeaderHovered, IM_COL32(0, 0, 100, 0xFF));
        ImGui::PushFont(FontFor(false), PointsToPixels(kDefaultPoints));
        look.colours = 4;
        return look;
    }

    const float text = PointsToPixels(t.text_points);
    const float gap = std::max(0.0f, kMenuItemHeight - text);
    const bool painted = t.menu_back.image != nullptr || t.menu_back.layers != nullptr;
    ImGui::PushStyleColor(ImGuiCol_PopupBg, painted ? kClear : t.menu_back.colour);
    ImGui::PushStyleColor(ImGuiCol_Text, t.menu_text);
    ImGui::PushStyleColor(ImGuiCol_Border, TooltipBorder(t));
    ImGui::PushStyleColor(ImGuiCol_HeaderHovered, t.menu_hover);
    ImGui::PushStyleColor(ImGuiCol_Header, t.menu_hover);
    ImGui::PushStyleColor(ImGuiCol_HeaderActive, t.menu_hover);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(kMenuInset + kMenuLeftArea, kMenuInset + gap * 0.5f));
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(ImGui::GetStyle().ItemSpacing.x, gap));
    ImGui::PushStyleVar(ImGuiStyleVar_PopupBorderSize, 1.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_PopupRounding, 0.0f);
    ImGui::PushFont(FontFor(false), text);
    look.colours = 6;
    look.vars = 4;
    return look;
}

void PopMenuLook(const MenuLook& look) {
    ImGui::PopFont();
    ImGui::PopStyleVar(look.vars);
    ImGui::PopStyleColor(look.colours);
}

// Inside each of a menu's popups, once begun: its background where that is a surface rather
// than a colour, and the strip down its left.
void DressMenu(const Theme& t) {
    if (!IsSet(t.menu_back)) return;
    PaintPopupBack(t.menu_back);
    if (t.menu_left == kClear) return;
    const ImVec2 at = ImGui::GetWindowPos();
    const ImVec2 size = ImGui::GetWindowSize();
    ImGui::GetWindowDrawList()->AddRectFilled(ImVec2(at.x + kMenuInset, at.y + kMenuInset),
                                              ImVec2(at.x + kMenuInset + kMenuLeftArea, at.y + size.y - kMenuInset), Col(t.menu_left));
}

// The title-bar icon's menu, as VVS's was: the themes, then what each title-bar button does.
void DrawWindowMenu(WindowMemory& memory, const View& view, const Theme& theme, bool ghosted, bool click_through, bool& open,
                    const std::string& owner, std::vector<Command>& commands) {
    const MenuLook look = PushMenuLook(theme);
    if (ImGui::BeginPopup("window-menu")) {
        DressMenu(theme);
        if (ImGui::BeginMenu("Change Theme")) {
            DressMenu(theme);
            if (ImGui::MenuItem("(Reset Theme)")) {
                memory.theme.clear();
                ImGui::MarkIniSettingsDirty();
            }
            for (const Theme* each : kThemes) {
                if (ImGui::MenuItem(each->name, nullptr, each == &theme)) {
                    memory.theme = each->name;
                    ImGui::MarkIniSettingsDirty();
                }
            }
            ImGui::EndMenu();
        }
        if (view.minimizable && ImGui::MenuItem("Minimize")) open = false;
        if (theme.alpha_buttons && view.alpha_changeable) {
            if (ImGui::MenuItem("Alpha Up")) memory.alpha = std::min(kAlphaMax, memory.alpha + kAlphaStep);
            if (ImGui::MenuItem("Alpha Down")) memory.alpha = std::max(kAlphaMin, memory.alpha - kAlphaStep);
        }
        if (view.ghostable && ImGui::MenuItem("Toggle Ghost")) {
            memory.ghosted = !ghosted;
            ImGui::MarkIniSettingsDirty();
            if (!ghosted) SayHudifiedOnce(view.title, commands);
        }
        if (ghosted && view.click_throughable && ImGui::MenuItem(click_through ? "Disable Clickthrough" : "Enable Clickthrough")) {
            memory.click_through = !click_through;
            ImGui::MarkIniSettingsDirty();
        }
        for (const TitleButton& own : view.title_buttons) {
            if (ImGui::MenuItem(own.tooltip.empty() ? own.name.c_str() : own.tooltip.c_str()))
                PressTitleButton(owner, own.name, commands);
        }
        ImGui::EndPopup();
    }
    PopMenuLook(look);
}

}  // namespace

float DecalScale() { return kScale; }

void SetDecalReveal(bool held) { g_reveal = held; }

bool DecalRevealHeld() { return g_reveal; }

void SetDecalDefaultTheme(const std::string& name) { g_host_default_theme = name; }

std::string DecalGlobalTheme() { return GlobalTheme().name; }

void CycleDecalGlobalTheme() {
    // VVS's NextTheme: the theme after this one in the order VVS registered them - Minimalist,
    // Float, Minimalist Transparent, Decal, Minimalist Black, Minimalist Green - round to the
    // first again; and from one it never registered, the hot-dog stand, the first.
    const Theme* current = &GlobalTheme();
    size_t next = 0;
    for (size_t i = 0; i < std::size(kThemes); ++i)
        if (kThemes[i] == current) next = (i + 1) % std::size(kThemes);
    Memory()[kGlobalThemeOwner].theme = kThemes[next]->name;
    ImGui::MarkIniSettingsDirty();
}

void SetDecalGlobalTheme(const std::string& name) {
    Memory()[kGlobalThemeOwner].theme = ThemeNamed(name).name;
    ImGui::MarkIniSettingsDirty();
}

void DrawDecalWindow(const PluginWindow& window, size_t cascade, bool& open, std::vector<Command>& commands) {
    const View& view = window.view;
    const float s = kScale;
    WindowMemory& memory = Memory()[window.owner];
    const Theme& t = ThemeOf(memory, view);
    const bool ghosted = GhostedOf(memory, view);
    const bool click_through = ghosted && ClickThroughOf(memory, view);

    // A hudified window shows its frame, title and buttons only while left Ctrl is held;
    // the rest of the time it is its body alone, and cannot be moved.
    const bool chrome = !ghosted || g_reveal;

    // VVS's layout: the frame, the title bar with its rule under it, and the view.
    const float border = view.resizeable ? t.border_resizeable : t.border;
    const float head = t.title_bar + t.title_rule;
    const float width = (static_cast<float>(std::max(view.width, 40)) + border * 2.0f) * s;
    const float height = (static_cast<float>(std::max(view.height, 20)) + head + border * 2.0f) * s;

    const ImGuiViewport* viewport = ImGui::GetMainViewport();
    const float step = 32.0f * s * static_cast<float>(cascade);
    const ImVec2 first = view.has_position
        ? ImVec2(viewport->Pos.x + static_cast<float>(view.x), viewport->Pos.y + static_cast<float>(view.y))
        : ImVec2(viewport->WorkPos.x + 48.0f * s + step, viewport->WorkPos.y + 64.0f * s + step);
    ImGui::SetNextWindowPos(first, ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(width, height), ImGuiCond_Always);

    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0, 0));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0.0f);
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, static_cast<float>(memory.alpha) / 255.0f);

    // Moved by its title bar alone, as a Decal window was; ImGui would otherwise move it
    // from any empty spot, which on a window of parchment is everywhere. A hudified window
    // sits behind the others, as VVS put it; a click-through one takes no clicks at all
    // until left Ctrl is held.
    ImGuiWindowFlags flags = ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
                             ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse |
                             ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoBackground;
    if (ghosted) flags |= ImGuiWindowFlags_NoBringToFrontOnFocus;
    if (click_through && !g_reveal) flags |= ImGuiWindowFlags_NoInputs;

    std::string label = "###decal:" + window.owner;
    if (ImGui::Begin(label.c_str(), nullptr, flags)) {
        ImDrawList* draw = ImGui::GetWindowDrawList();
        ImVec2 wa = ImGui::GetWindowPos();
        if (ghosted)
            wa = KeepOnScreen(memory, wa, ImVec2(width, height), border * s, head * s);
        const ImVec2 wb(wa.x + width, wa.y + height);
        const float e = border * s;
        const ImVec2 ia(wa.x + e, wa.y + e);
        const ImVec2 ib(wb.x - e, wb.y - e);
        const ImVec2 body(ia.x, ia.y + head * s);

        // Black under everything the window covers, as VVS cleared a view's texture before
        // drawing it: all of it, or for a hudified window its body alone. But VVS filled its
        // colours into that texture rather than laying them on it - a colour replaced what was
        // under it, alpha and all - so a theme whose colours are glass, Minimalist
        // Transparent, shows the game through them rather than the black.
        if (t.black_under) draw->AddRectFilled(chrome ? wa : body, chrome ? wb : ib, Col(IM_COL32(0, 0, 0, 0xFF)));

        // The title bar as VVS drew it for the view on top, ViewTopBackground_Selected, where
        // the theme tells the two apart; the title's colour and shadow follow.
        const bool focused = ImGui::IsWindowFocused(ImGuiFocusedFlags_RootAndChildWindows);

        if (chrome) {
            DrawWindowFrame(draw, t, wa, wb, border, s);
            Paint(draw, focused ? Either(t.top_focused, t.top) : t.top, ia, ImVec2(ib.x, ia.y + t.title_bar * s), s);
            Paint(draw, t.title_rule_fill, ImVec2(ia.x, ia.y + t.title_bar * s), ImVec2(ib.x, body.y), s);

            // The buttons, right to left from the frame: close; the two alpha buttons where
            // the theme has them; the pin that hudifies; and on a hudified window, the arrow
            // that makes it click-through.
            const float button = t.button_size * s;
            float offset = (t.buttons_left + border) * s;
            const float by = wa.y + (t.buttons_down + border) * s;
            auto next = [&]() {
                const ImVec2 at(wb.x - (offset + button), by);
                offset += button + t.button_spacing * s;
                return at;
            };

            float leftmost = ib.x;
            if (view.minimizable) {
                const ImVec2 close_at = next();
                if (DrawTitleButton("close", Face{t.close_up, t.close_down}, close_at, button, "Minimize", t)) open = false;
                leftmost = close_at.x;
            }
            if (t.alpha_buttons && view.alpha_changeable) {
                const ImVec2 up_at = next();
                if (DrawTitleButton("alpha-up", t.alpha_up, up_at, button, "Alpha Up", t))
                    memory.alpha = std::min(kAlphaMax, memory.alpha + kAlphaStep);
                const ImVec2 down_at = next();
                if (DrawTitleButton("alpha-down", t.alpha_down, down_at, button, "Alpha Down", t))
                    memory.alpha = std::max(kAlphaMin, memory.alpha - kAlphaStep);
                leftmost = down_at.x;
            }
            if (view.ghostable) {
                const ImVec2 ghost_at = next();
                if (DrawTitleButton("ghost", t.ghost, ghost_at, button, "Toggle Ghost", t)) {
                    memory.ghosted = !ghosted;
                    ImGui::MarkIniSettingsDirty();
                    if (!ghosted) SayHudifiedOnce(!view.title.empty() ? view.title : window.owner, commands);
                }
                leftmost = ghost_at.x;
            }
            if (ghosted && view.click_throughable) {
                const ImVec2 ct_at = next();
                const bool on = click_through;
                if (DrawTitleButton("click-through", on ? t.click_on : t.click_off, ct_at, button, on ? "Disable Clickthrough" : "Enable Clickthrough", t)) {
                    memory.click_through = !on;
                    ImGui::MarkIniSettingsDirty();
                }
                leftmost = ct_at.x;
            }
            for (const TitleButton& own : view.title_buttons) {
                const ImVec2 at = next();
                ImGui::PushID(own.name.c_str());
                const char* down = own.image_down.empty() ? own.image.c_str() : own.image_down.c_str();
                const Face face{{own.image.c_str(), kClear}, {down, kClear}};
                if (DrawTitleButton("own", face, at, button, own.tooltip.empty() ? nullptr : own.tooltip.c_str(), t))
                    PressTitleButton(window.owner, own.name, commands);
                ImGui::PopID();
                leftmost = at.x;
            }

            // The icon opens the window's menu.
            const ImVec2 icon_a(ia.x + t.icon_x * s, ia.y + t.icon_y * s);
            const ImVec2 icon_b(icon_a.x + t.icon_size * s, icon_a.y + t.icon_size * s);
            ImU32 swatch = 0;
            if (ColourKey(view.icon, swatch)) draw->AddRectFilled(icon_a, icon_b, Col(swatch));
            else if (const Texture* icon = FindTexture(view.icon)) DrawImage(draw, icon, icon_a, icon_b);
            if (Hit("icon", icon_a, icon_b)) ImGui::OpenPopup("window-menu");
            DrawWindowMenu(memory, view, t, ghosted, click_through, open, window.owner, commands);

            // The title: Decal's bold at the left after the icon, Float's plain and centred,
            // in a line sixteen pixels high that stops as far short of the right as it starts
            // from the left.
            const std::string& title = !view.title.empty() ? view.title : !window.title.empty() ? window.title : window.owner;
            ImFont* title_font = FontFor(t.title_bold);
            const float title_size = PointsToPixels(t.title_points) * s;
            const float tx = ia.x + t.title_x * s;
            const float ty = ia.y + t.title_y * s;
            const float tw = std::max(0.0f, (ib.x - ia.x) - t.title_x * 2.0f * s);
            const ImVec4 title_clip(tx, ty, std::min(tx + tw, leftmost), ty + 16.0f * s);
            const float measured = Measure(title_font, title_size, title).x;
            const float x = t.title_centred ? tx + std::max(0.0f, (tw - measured) * 0.5f) : tx;
            HaloText(draw, title_font, title_size, ImVec2(x, ty), focused ? t.title_text_focused : t.title_text,
                     focused ? t.title_halo_focused : t.title_halo, title, &title_clip);

            // Dragging the title bar moves the window.
            Hit("title", ia, ImVec2(leftmost, ia.y + t.title_bar * s));
            if (ImGui::IsItemActive() && ImGui::IsMouseDragging(ImGuiMouseButton_Left, 0.0f)) {
                const ImVec2 delta = ImGui::GetIO().MouseDelta;
                ImVec2 moved(wa.x + delta.x, wa.y + delta.y);
                if (ghosted) {
                    memory.stuck.clear();
                    moved = KeepOnScreen(memory, moved, ImVec2(width, height), border * s, head * s, true);
                }
                ImGui::SetWindowPos(moved);
            }
        }

        // The view itself, under the title bar.
        Frame frame{window.owner, draw, body, s, commands, memory, t};
        const Rect whole{0.0f, 0.0f, static_cast<float>(view.width), static_cast<float>(view.height)};
        ImGui::PushClipRect(frame.origin, ib, true);
        ImGui::PushID(window.owner.c_str());
        DrawControl(frame, view.root, whole, 0);
        ImGui::PopID();
        ImGui::PopClipRect();

        // Every item was placed by position; one last item at the origin keeps ImGui's
        // idea of the window's contents inside the window.
        ImGui::SetCursorScreenPos(wa);
        ImGui::Dummy(ImVec2(1.0f, 1.0f));
    }
    ImGui::End();
    ImGui::PopStyleVar(3);
}

// The player's choices for each window, in the overlay's ini beside where each was left:
// [VVSView][owner] with Theme=, Ghost=, ClickThrough= and Alpha= lines, only what differs
// from the host's say.
void RegisterDecalSettings() {
    if (ImGui::FindSettingsHandler("VVSView") != nullptr) return;

    ImGuiSettingsHandler handler;
    handler.TypeName = "VVSView";
    handler.TypeHash = ImHashStr("VVSView");
    handler.ReadOpenFn = [](ImGuiContext*, ImGuiSettingsHandler*, const char* name) -> void* {
        return &Memory()[name];
    };
    handler.ReadLineFn = [](ImGuiContext*, ImGuiSettingsHandler*, void* entry, const char* line) {
        auto* memory = static_cast<WindowMemory*>(entry);
        const std::string_view text(line);
        const size_t equals = text.find('=');
        if (equals == std::string_view::npos) return;
        const std::string_view key = text.substr(0, equals);
        const std::string value(text.substr(equals + 1));
        if (key == "Theme") memory->theme = value;
        else if (key == "Ghost") memory->ghosted = value == "1";
        else if (key == "ClickThrough") memory->click_through = value == "1";
        else if (key == "Alpha") memory->alpha = std::clamp(static_cast<int>(ParseNumber(value, kAlphaMax)), kAlphaMin, kAlphaMax);
        else if (key == "Stuck") memory->stuck = value;
    };
    handler.WriteAllFn = [](ImGuiContext*, ImGuiSettingsHandler* self, ImGuiTextBuffer* out) {
        for (const auto& [owner, memory] : Memory()) {
            const bool any = !memory.theme.empty() || memory.ghosted.has_value() || memory.click_through.has_value() || memory.alpha != kAlphaMax
                             || !memory.stuck.empty();
            if (!any || owner.empty()) continue;
            out->appendf("[%s][%s]\n", self->TypeName, owner.c_str());
            if (!memory.theme.empty()) out->appendf("Theme=%s\n", memory.theme.c_str());
            if (memory.ghosted.has_value()) out->appendf("Ghost=%d\n", *memory.ghosted ? 1 : 0);
            if (memory.click_through.has_value()) out->appendf("ClickThrough=%d\n", *memory.click_through ? 1 : 0);
            if (memory.alpha != kAlphaMax) out->appendf("Alpha=%d\n", memory.alpha);
            if (!memory.stuck.empty()) out->appendf("Stuck=%s\n", memory.stuck.c_str());
            out->append("\n");
        }
    };
    ImGui::AddSettingsHandler(&handler);
}

DecalWindowLook DescribeDecalWindow(const std::string& owner, const View& view) {
    auto found = Memory().find(owner);
    static const WindowMemory kNone;
    const WindowMemory& memory = found != Memory().end() ? found->second : kNone;
    DecalWindowLook look;
    look.theme = ThemeOf(memory, view).name;
    look.ghosted = GhostedOf(memory, view);
    look.click_through = look.ghosted && ClickThroughOf(memory, view);
    look.alpha = memory.alpha;
    return look;
}

void EndDecalFrame() {
    const int frame = ImGui::GetFrameCount();
    for (auto& [owner, memory] : Memory()) {
        for (auto edit = memory.edits.begin(); edit != memory.edits.end();) {
            if (edit->second.frame != frame) edit = memory.edits.erase(edit);
            else ++edit;
        }
    }
}

bool DecalBarArrow(const char* id, bool expanded, const char* tooltip) {
    const float size = 16.0f * kScale;
    const ImVec2 a = ImGui::GetCursorScreenPos();
    const ImVec2 b(a.x + size, a.y + size);

    const bool released = ImGui::InvisibleButton(id, ImVec2(size, size));
    const bool hovered = ImGui::IsItemHovered();
    const bool held = ImGui::IsItemActive();

    // A press that goes on to drag moves the bar and is not a click. The drag is measured
    // from where the press began, so the bar follows the pointer exactly rather than
    // losing the few pixels ImGui waits before it calls a movement a drag.
    static bool dragged = false;
    if (ImGui::IsItemActivated()) dragged = false;
    if (held && ImGui::IsMouseDragging(ImGuiMouseButton_Left)) {
        const ImVec2 delta = ImGui::GetMouseDragDelta(ImGuiMouseButton_Left);
        ImGui::ResetMouseDragDelta(ImGuiMouseButton_Left);
        const ImVec2 at = ImGui::GetWindowPos();
        ImGui::SetWindowPos(ImVec2(at.x + delta.x, at.y + delta.y));
        dragged = true;
    }

    if (!held) ThemedTooltip(kDecalTheme, tooltip);

    // Pointing the way a click will send the switches: left to fold them away, right to
    // bring them back.
    const bool pressed = held && hovered;
    const char* key = expanded ? (pressed ? kBarFoldPressed : kBarFold) : (pressed ? kBarUnfoldPressed : kBarUnfold);

    ImDrawList* draw = ImGui::GetWindowDrawList();
    if (const Texture* tex = FindTexture(key)) {
        DrawImage(draw, tex, a, b);
    } else {
        draw->AddRectFilled(a, b, Col(IM_COL32(0xB4, 0x94, 0x5C, 255)));
        const float l = a.x + size * 0.3f;
        const float r = a.x + size * 0.7f;
        const ImVec2 top(expanded ? r : l, a.y + size * 0.25f);
        const ImVec2 bottom(expanded ? r : l, a.y + size * 0.75f);
        const ImVec2 tip(expanded ? l : r, a.y + size * 0.5f);
        draw->AddTriangleFilled(top, bottom, tip, Col(IM_COL32(0x5A, 0x3A, 0x14, 255)));
    }

    return released && !dragged;
}

namespace {

// A press on an item of a bar: a drag moves the bar's whole window - along the axes allowed,
// or not at all - and anything else is a click. The drag is measured from where the press
// began, so the bar follows the pointer exactly.
bool ClickOrDrag(const char* id, ImVec2 size, bool& hovered, bool& held, BarDrag drag = BarDrag::Free) {
    const bool released = ImGui::InvisibleButton(id, size);
    hovered = ImGui::IsItemHovered();
    held = ImGui::IsItemActive();

    static ImGuiID dragging = 0;
    const ImGuiID me = ImGui::GetItemID();
    if (ImGui::IsItemActivated() && dragging == me) dragging = 0;
    if (drag != BarDrag::None && held && ImGui::IsMouseDragging(ImGuiMouseButton_Left)) {
        ImVec2 delta = ImGui::GetMouseDragDelta(ImGuiMouseButton_Left);
        ImGui::ResetMouseDragDelta(ImGuiMouseButton_Left);
        if (drag == BarDrag::Across) delta.y = 0.0f;
        if (drag == BarDrag::Down) delta.x = 0.0f;
        const ImVec2 at = ImGui::GetWindowPos();
        ImGui::SetWindowPos(ImVec2(at.x + delta.x, at.y + delta.y));
        dragging = me;
    }

    const bool clicked = released && dragging != me;
    if (released) dragging = 0;
    return clicked;
}

}  // namespace

bool DecalBarGrip(const char* id, const char* tooltip, bool vertical, BarDrag drag) {
    // Two bars, as Decal drew them at each end of its bar: a pale face and a darker edge,
    // upright on a bar across the top and lying down on one down a side.
    const ImVec2 size = vertical ? ImVec2(18.0f, 6.0f) : ImVec2(6.0f, 18.0f);
    const ImVec2 a = ImGui::GetCursorScreenPos();

    bool hovered = false;
    bool held = false;
    const bool clicked = ClickOrDrag(id, size, hovered, held, drag);
    if (!held) ThemedTooltip(kDecalTheme, tooltip);

    ImDrawList* draw = ImGui::GetWindowDrawList();
    const ImU32 face = IM_COL32(0xE6, 0xC2, 0xB4, 0xFF);
    const ImU32 edge = IM_COL32(0x9A, 0x6A, 0x5E, 0xFF);
    for (int bar = 0; bar < 2; ++bar) {
        const float o = 1.0f + bar * 3.0f;
        if (!vertical) {
            draw->AddRectFilled(ImVec2(a.x + o, a.y + 2.0f), ImVec2(a.x + o + 1.0f, a.y + size.y - 2.0f), Col(face));
            draw->AddRectFilled(ImVec2(a.x + o + 1.0f, a.y + 2.0f), ImVec2(a.x + o + 2.0f, a.y + size.y - 2.0f), Col(edge));
        } else {
            draw->AddRectFilled(ImVec2(a.x + 2.0f, a.y + o), ImVec2(a.x + size.x - 2.0f, a.y + o + 1.0f), Col(face));
            draw->AddRectFilled(ImVec2(a.x + 2.0f, a.y + o + 1.0f), ImVec2(a.x + size.x - 2.0f, a.y + o + 2.0f), Col(edge));
        }
    }

    return clicked;
}

bool DecalBarButton(const char* id, const char* up, const char* down, const char* tooltip) {
    const float size = 16.0f;
    const ImVec2 a = ImGui::GetCursorScreenPos();
    const ImVec2 b(a.x + size, a.y + size);
    const bool clicked = ImGui::InvisibleButton(id, ImVec2(size, size));
    const bool held = ImGui::IsItemActive() && ImGui::IsItemHovered();
    if (!ImGui::IsItemActive()) ThemedTooltip(kDecalTheme, tooltip);

    ImDrawList* draw = ImGui::GetWindowDrawList();
    if (const Texture* tex = FindTexture(held ? down : up)) DrawImage(draw, tex, a, b);
    else draw->AddRectFilled(a, b, Col(IM_COL32(0xB4, 0x94, 0x5C, 0xFF)));
    return clicked;
}

bool DecalIconSwitch(const char* id, const std::string& icon, SwitchLook look, const char* tooltip) {
    const float size = 18.0f;
    const ImVec2 a = ImGui::GetCursorScreenPos();
    const ImVec2 b(a.x + size, a.y + size);

    bool hovered = false;
    bool held = false;
    const bool clicked = ClickOrDrag(id, ImVec2(size, size), hovered, held, BarDrag::None);
    if (!held) ThemedTooltip(kDecalTheme, tooltip);

    ImDrawList* draw = ImGui::GetWindowDrawList();

    // A square from the middle of Decal's switch bitmap: 100 by 20 for the gold and red, the
    // grey one a 64-wide tab whose ends are the cyan Decal keyed out.
    const char* key = look == SwitchLook::Open ? kSwitchActiveTexture
                    : look == SwitchLook::Closed ? kSwitchInactiveTexture
                                                 : kSwitchDisabledTexture;
    const ImVec2 inner_a(a.x + 1.0f, a.y + 1.0f);
    const ImVec2 inner_b(b.x - 1.0f, b.y - 1.0f);
    if (const Texture* tex = FindTexture(key); tex != nullptr && tex->width > 0.0f) {
        const float u0 = look == SwitchLook::Faulted ? 24.0f / tex->width : 40.0f / tex->width;
        const float u1 = u0 + 16.0f / tex->width;
        const ImU32 tint = Col(hovered && !held ? IM_COL32(255, 255, 225, 255) : IM_COL32_WHITE);
        draw->AddImage(tex->ref, inner_a, inner_b, ImVec2(u0, 2.0f / 20.0f), ImVec2(u1, 18.0f / 20.0f), tint);
    } else {
        const ImU32 fill = look == SwitchLook::Open ? IM_COL32(0xB4, 0x7E, 0x2B, 255)
                         : look == SwitchLook::Closed ? IM_COL32(0x8A, 0x3A, 0x22, 255)
                                                      : IM_COL32(0x80, 0x80, 0x80, 255);
        draw->AddRectFilled(inner_a, inner_b, Col(fill));
    }

    draw->AddRect(a, b, Col(IM_COL32(0x10, 0x0C, 0x08, 0xFF)));

    if (const Texture* tex = FindTexture(icon)) DrawImage(draw, tex, inner_a, inner_b);
    return clicked;
}

bool DecalLabelSwitch(const char* id, const std::string& label, const std::string& icon, SwitchLook look, float width,
                      const char* tooltip) {
    // Decal's switch in its expanded form: the whole switch bitmap - gold open, red closed -
    // stretched to its width and 20 high, the plugin's icon at its left and its name after.
    const float h = 20.0f;
    const ImVec2 a = ImGui::GetCursorScreenPos();
    const ImVec2 b(a.x + width, a.y + h);

    bool hovered = false;
    bool held = false;
    const bool clicked = ClickOrDrag(id, ImVec2(width, h), hovered, held, BarDrag::None);
    if (!held) ThemedTooltip(kDecalTheme, tooltip);

    ImDrawList* draw = ImGui::GetWindowDrawList();
    const char* key = look == SwitchLook::Open ? kSwitchActiveTexture : kSwitchInactiveTexture;
    const ImU32 tint = Col(look == SwitchLook::Faulted ? IM_COL32(150, 150, 150, 255)
                           : hovered && !held ? IM_COL32(255, 255, 225, 255) : IM_COL32_WHITE);
    if (const Texture* tex = FindTexture(key)) {
        draw->AddImage(tex->ref, a, b, ImVec2(0, 0), ImVec2(1, 1), tint);
    } else {
        draw->AddRectFilled(a, b, Col(look == SwitchLook::Open ? IM_COL32(0xB4, 0x7E, 0x2B, 255) : IM_COL32(0x8A, 0x3A, 0x22, 255)));
    }

    const ImVec2 icon_a(a.x + 2.0f, a.y + 2.0f);
    if (const Texture* tex = FindTexture(icon)) DrawImage(draw, tex, icon_a, ImVec2(icon_a.x + 16.0f, icon_a.y + 16.0f));

    ImFont* font = FontFor(true);
    const float size = PointsToPixels(kDefaultPoints);
    const ImVec4 clip(a.x + 20.0f, a.y, b.x - 2.0f, b.y);
    const ImU32 text = look == SwitchLook::Open ? IM_COL32(0, 0, 0, 255) : IM_COL32(0xF0, 0xE4, 0xCC, 255);
    Text(draw, font, size, ImVec2(a.x + 20.0f, a.y + (h - size) * 0.5f), text, label, &clip);
    return clicked;
}

bool VvsBarItem(const char* id, const std::string& icon, bool open, const char* tooltip, bool movable) {
    // VVS's bar cell: twenty pixels, the sixteen-pixel icon two in, and under an open view's
    // icon an eighteen-pixel square in the theme's button shadow. No hover mark: VVS had none.
    const float cell = 20.0f;
    const ImVec2 a = ImGui::GetCursorScreenPos();

    bool hovered = false;
    bool held = false;
    const bool clicked = ClickOrDrag(id, ImVec2(cell, cell), hovered, held, movable ? BarDrag::Free : BarDrag::None);
    if (!held) ThemedTooltip(BarTheme(), tooltip);

    ImDrawList* draw = ImGui::GetWindowDrawList();
    const Theme& theme = BarTheme();
    if (open)   // Hint_VVSBarItemUnderlay
        draw->AddRectFilled(ImVec2(a.x + 1.0f, a.y + 1.0f), ImVec2(a.x + 19.0f, a.y + 19.0f),
                            Col(theme.bar_underlay != kClear ? theme.bar_underlay : theme.button_shadow));

    const ImVec2 ia(a.x + 2.0f, a.y + 2.0f);
    const ImVec2 ib(a.x + 18.0f, a.y + 18.0f);
    ImU32 swatch = 0;
    if (ColourKey(icon, swatch)) {
        draw->AddRectFilled(ia, ib, Col(swatch));
    } else if (const Texture* tex = FindTexture(icon)) {
        DrawImage(draw, tex, ia, ib);
    } else {
        // No icon: a plain tile, so the plugin is still there to click.
        draw->AddRectFilled(ia, ib, Col(IM_COL32(0x8C, 0x50, 0x1E, 0xFF)));
        draw->AddRect(ia, ib, Col(IM_COL32(0x46, 0x28, 0x0F, 0xFF)));
    }

    return clicked;
}

bool VvsBarBox(bool across, bool shown) {
    // The bar's title-bar icon, VVS's ACImage(Color.Coral): a press opens its window menu, as any
    // VVS window's icon did - Change Theme for the bar itself, and the blue arrow's own entry.
    // The menu stays open when Ctrl is let go and the box goes.
    if (shown) {
        const ImVec2 a = ImGui::GetCursorScreenPos();
        const bool clicked = ImGui::InvisibleButton("vvsbar-box", ImVec2(20.0f, 20.0f));
        ImGui::GetWindowDrawList()->AddRectFilled(ImVec2(a.x + 2.0f, a.y + 2.0f), ImVec2(a.x + 18.0f, a.y + 18.0f),
                                                  Col(IM_COL32(0xFF, 0x7F, 0x50, 0xFF)));
        if (clicked) ImGui::OpenPopup("vvsbar-menu");
    }

    bool turn = false;
    const Theme& theme = BarTheme();
    const MenuLook look = PushMenuLook(theme);
    if (ImGui::BeginPopup("vvsbar-menu")) {
        DressMenu(theme);
        WindowMemory& memory = Memory()[kVvsBarOwner];
        if (ImGui::BeginMenu("Change Theme")) {
            DressMenu(theme);
            if (ImGui::MenuItem("(Reset Theme)")) {
                memory.theme.clear();
                ImGui::MarkIniSettingsDirty();
            }
            for (const Theme* each : kThemes) {
                if (ImGui::MenuItem(each->name, nullptr, !memory.theme.empty() && each == &theme)) {
                    memory.theme = each->name;
                    ImGui::MarkIniSettingsDirty();
                }
            }
            ImGui::EndMenu();
        }
        if (ImGui::MenuItem(across ? "Set Vertical" : "Set Horizontal")) turn = true;
        ImGui::EndPopup();
    }
    PopMenuLook(look);
    return turn;
}

std::string DecalBarTheme() { return BarTheme().name; }

void VvsBarRule(bool vertical) {
    // Between one plugin's views and the next's: four pixels, the theme's button shadow a
    // pixel in and its highlight after, sixteen long and two in from each side.
    const ImVec2 a = ImGui::GetCursorScreenPos();
    ImGui::Dummy(vertical ? ImVec2(20.0f, 4.0f) : ImVec2(4.0f, 20.0f));
    ImDrawList* draw = ImGui::GetWindowDrawList();
    const Theme& t = BarTheme();
    if (vertical) {
        draw->AddRectFilled(ImVec2(a.x + 2.0f, a.y + 1.0f), ImVec2(a.x + 18.0f, a.y + 2.0f), Col(t.button_shadow));
        draw->AddRectFilled(ImVec2(a.x + 2.0f, a.y + 2.0f), ImVec2(a.x + 18.0f, a.y + 3.0f), Col(t.button_highlight));
    } else {
        draw->AddRectFilled(ImVec2(a.x + 1.0f, a.y + 2.0f), ImVec2(a.x + 2.0f, a.y + 18.0f), Col(t.button_shadow));
        draw->AddRectFilled(ImVec2(a.x + 2.0f, a.y + 2.0f), ImVec2(a.x + 3.0f, a.y + 18.0f), Col(t.button_highlight));
    }
}

bool DecalSwitch(const char* id, const std::string& label, const std::string& icon, SwitchLook look, const char* tooltip) {
    const float s = kScale;
    const Texture* pill = FindTexture(look == SwitchLook::Open ? kSwitchOpen : look == SwitchLook::Closed ? kSwitchClosed : kSwitchFaulted);

    ImFont* font = FontFor(true);
    const float size = PointsToPixels(kDefaultPoints) * s;
    const float h = 20.0f * s;
    const float icon_box = 16.0f * s;
    const float left = 20.0f * s;   // the pill's concave end, where the icon sits
    const float right = 10.0f * s;  // its rounded end
    const float w = left + Measure(font, size, label).x + right + 4.0f * s;

    const ImVec2 a = ImGui::GetCursorScreenPos();
    const ImVec2 b(a.x + w, a.y + h);
    const bool clicked = ImGui::InvisibleButton(id, ImVec2(w, h));
    const bool hovered = ImGui::IsItemHovered();
    ThemedTooltip(kDecalTheme, tooltip);

    ImDrawList* draw = ImGui::GetWindowDrawList();
    if (pill != nullptr && pill->width > 30.0f) {
        const float ul = 20.0f / pill->width;
        const float ur = 1.0f - 10.0f / pill->width;
        const ImU32 tint = Col(hovered ? IM_COL32(255, 255, 230, 255) : IM_COL32_WHITE);
        draw->AddImage(pill->ref, a, ImVec2(a.x + left, b.y), ImVec2(0, 0), ImVec2(ul, 1), tint);
        draw->AddImage(pill->ref, ImVec2(a.x + left, a.y), ImVec2(b.x - right, b.y), ImVec2(ul, 0), ImVec2(ur, 1), tint);
        draw->AddImage(pill->ref, ImVec2(b.x - right, a.y), b, ImVec2(ur, 0), ImVec2(1, 1), tint);
    } else {
        const ImU32 fill = look == SwitchLook::Open ? IM_COL32(0xB4, 0x7E, 0x2B, 255)
                         : look == SwitchLook::Closed ? IM_COL32(0x8A, 0x3A, 0x22, 255)
                                                      : IM_COL32(0x80, 0x80, 0x80, 255);
        draw->AddRectFilled(a, b, Col(fill), h * 0.5f);
    }

    if (const Texture* tex = FindTexture(icon))
        DrawImage(draw, tex, ImVec2(a.x + 2.0f * s, a.y + (h - icon_box) * 0.5f),
                  ImVec2(a.x + 2.0f * s + icon_box, a.y + (h + icon_box) * 0.5f));

    const ImU32 text = look == SwitchLook::Open ? IM_COL32(0, 0, 0, 255) : IM_COL32(0xF0, 0xE4, 0xCC, 255);
    Text(draw, font, size, ImVec2(a.x + left + 2.0f * s, a.y + (h - size) * 0.5f), text, label);
    return clicked;
}

}  // namespace overlay
