# Game Launcher — UI & Visual Overhaul Spec
> Visual styling guide for updating the existing iscLauncher application.

---

## Project Overview

Visual theme: **fantasy/modern** — warm dark backgrounds, aged-gold accents, emerald greens for status and actions, parchment tones for text. Typography uses serif display fonts for headings and clean sans-serif for UI labels.

---

## Design Tokens

Define these as static resources in `App.xaml` under `Application.Resources`:

```xml
<!-- Backgrounds -->
<Color x:Key="BgColor">#12100E</Color>
<Color x:Key="SurfaceColor">#1C1813</Color>
<Color x:Key="Surface2Color">#221D17</Color>
<Color x:Key="Surface3Color">#2B2419</Color>

<!-- Accents -->
<Color x:Key="GoldColor">#C9A84C</Color>
<Color x:Key="GoldLightColor">#E8C97A</Color>
<Color x:Key="GoldDarkColor">#8A6E2F</Color>
<Color x:Key="EmeraldColor">#3D7A5C</Color>
<Color x:Key="EmeraldLightColor">#5AAF85</Color>
<Color x:Key="CrimsonColor">#9B3A3A</Color>
<Color x:Key="ParchmentColor">#D4B896</Color>

<!-- Text -->
<Color x:Key="TextPrimaryColor">#E8DCC8</Color>
<Color x:Key="TextMutedColor">#8A7A65</Color>
<Color x:Key="TextDimColor">#4A3F30</Color>

<!-- Borders -->
<Color x:Key="BorderColor">#2D2418</Color>   <!-- ~15% gold opacity -->
<Color x:Key="BorderHighColor">#4A3820</Color> <!-- ~35% gold opacity -->

<!-- Brushes (reference the above colors) -->
<SolidColorBrush x:Key="BgBrush" Color="{StaticResource BgColor}"/>
<SolidColorBrush x:Key="SurfaceBrush" Color="{StaticResource SurfaceColor}"/>
<SolidColorBrush x:Key="GoldBrush" Color="{StaticResource GoldColor}"/>
<SolidColorBrush x:Key="EmeraldBrush" Color="{StaticResource EmeraldColor}"/>
<SolidColorBrush x:Key="TextPrimaryBrush" Color="{StaticResource TextPrimaryColor}"/>
<SolidColorBrush x:Key="TextMutedBrush" Color="{StaticResource TextMutedColor}"/>
<SolidColorBrush x:Key="BorderBrush" Color="{StaticResource BorderColor}"/>
<SolidColorBrush x:Key="BorderHighBrush" Color="{StaticResource BorderHighColor}"/>
```

---

## Fonts

Add the following font files to `/Assets/Fonts/` and declare them in `App.xaml`:

```xml
<FontFamily x:Key="DisplayFont">ms-appx:///Assets/Fonts/Cinzel-Bold.ttf#Cinzel</FontFamily>
<FontFamily x:Key="SerifFont">ms-appx:///Assets/Fonts/CrimsonPro-Regular.ttf#Crimson Pro</FontFamily>
<FontFamily x:Key="BodyFont">ms-appx:///Assets/Fonts/DMSans-Regular.ttf#DM Sans</FontFamily>
```

**Usage rules:**
- Headings, labels, button text → `{StaticResource DisplayFont}` (Cinzel)
- Flavor text, descriptions, subtitles → `{StaticResource SerifFont}` (Crimson Pro), FontStyle="Italic"
- Settings keys, version strings, small UI labels → `{StaticResource BodyFont}` (DM Sans)

---

## Window Setup

In `MainWindow.xaml`:

```xml
<Window
    x:Class="GameLauncher.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Realm Vault"
    MinWidth="960" MinHeight="600">

    <!-- Mica backdrop for sidebar blending with desktop wallpaper -->
    <Window.SystemBackdrop>
        <MicaBackdrop Kind="Base"/>
    </Window.SystemBackdrop>

    <Grid>
        <!-- Custom title bar -->
        <Grid x:Name="AppTitleBar" Height="44" VerticalAlignment="Top"
              Background="#0F0D0B"/>
        <!-- Main content below title bar -->
        <Grid Margin="0,44,0,0">
            <!-- See layout section below -->
        </Grid>
    </Grid>
</Window>
```

In `MainWindow.xaml.cs` code-behind:
```csharp
ExtendsContentIntoTitleBar = true;
SetTitleBar(AppTitleBar);
```

---

## Layout Structure

The main content area is a two-column Grid:

```
┌─────────────────────────────────────────────────────┐
│  TitleBar (44px, full width)                        │
├──────────────┬──────────────────────────────────────┤
│              │  Hero Banner (270px tall)            │
│   Sidebar    │─────────────────────────────────────  │
│   (240px)    │  Game Card Grid (remaining height)  │
│              │                                      │
└──────────────┴──────────────────────────────────────┘
```

```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="240"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>  <!-- Hero -->
        <RowDefinition Height="*"/>     <!-- Card grid -->
    </Grid.RowDefinitions>

    <local:SidebarControl Grid.Column="0" Grid.RowSpan="2"/>
    <local:HeroBannerControl Grid.Column="1" Grid.Row="0"/>
    <local:GameGridControl Grid.Column="1" Grid.Row="1"/>
</Grid>
```

---

## Component Specifications

### 1. Game List Item Style

Each game in the left panel list:

**Structure:**
```xml
<Border Background="{StaticResource Surface2Brush}"
        BorderThickness="1"
        BorderBrush="{StaticResource BorderColor}"
        CornerRadius="6"
        Padding="12"
        Margin="8">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="48"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>

        <!-- Game Icon -->
        <Border Grid.Column="0" Width="48" Height="48" CornerRadius="4"
                BorderThickness="1" BorderBrush="{StaticResource BorderColor}">
            <Image Source="{Binding IconPath}"/>
            <!-- Fallback: First letter of game name -->
        </Border>

        <!-- Game Name -->
        <TextBlock Grid.Column="1" 
                   Text="{Binding Name}"
                   FontFamily="{StaticResource DisplayFont}"
                   FontSize="13"
                   Foreground="{StaticResource TextPrimaryBrush}"
                   VerticalAlignment="Center"
                   Margin="12,0,0,0"
                   TextTrimming="CharacterEllipsis"/>

        <!-- Status Indicator -->
        <Ellipse Grid.Column="2" 
                 Width="8" Height="8"
                 Fill="{StaticResource EmeraldBrush}"
                 Visibility="{Binding IsConfigured, Converter={StaticResource BoolToVisibilityConverter}}">
            <Ellipse.Effect>
                <DropShadow Color="#3D7A5C" BlurRadius="4" Opacity="0.6"/>
            </Ellipse.Effect>
        </Ellipse>
    </Grid>
</Border>
```

**Visual States:**
- **Normal:** Surface2 background, Border color border
- **Hover:** Background lightens to Surface3, cursor pointer
- **Selected:** BorderBrush=GoldDark, Background=Surface3, left border accent 3px gold
- **Disabled:** Opacity 0.5, no hover effect

**Icon Fallback:**
```xml
<!-- When no icon extracted -->
<Border Background="{StaticResource GoldDarkColor}" Opacity="0.2">
    <TextBlock Text="{Binding NameInitial}"
               FontFamily="{StaticResource DisplayFont}"
               FontSize="20"
               Foreground="{StaticResource GoldBrush}"
               Opacity="0.6"
               HorizontalAlignment="Center"
               VerticalAlignment="Center"/>
</Border>
```

### 2. Empty State

Selected state: `BorderBrush=GoldDark`, outer glow via `DropShadowEffect` (GoldDark, BlurRadius=16, Opacity=0.3)

Status indicator: 6px circle, emerald color with `DropShadow` glow

Title: Cinzel 11px, Subtitle: DM Sans 10px

### 3. Game Detail Panel (Right-Side)

**Purpose:** Display full game settings when a game card is clicked

**Layout:**
- Width: 400px fixed
- Slides in from right side with animation
- Background: `{StaticResource Surface2Brush}`
- Left border: 1px `{StaticResource BorderHighBrush}` with subtle gold glow
- Padding: 24px

**Structure (top to bottom):**

#### View Mode Structure
```xml
<!-- Game Name -->
<TextBlock Text="{Binding Name}" 
           FontFamily="{StaticResource DisplayFont}"
           FontSize="28"
           Foreground="{StaticResource TextPrimaryBrush}"/>

<!-- Ornamental Divider -->
<Rectangle Height="2" 
           Fill="{StaticResource GoldDarkColor}"
           Opacity="0.3"
           Width="120"
           HorizontalAlignment="Left"/>
```

#### Settings Display (View Mode)
```xml
<!-- Section Header -->
<TextBlock Text="⚔ CONNECTION" 
           FontFamily="{StaticResource DisplayFont}"
           FontSize="9"
           CharacterSpacing="150"
           Foreground="{StaticResource GoldDarkColor}"
           Margin="0,0,0,12"/>

<!-- Setting Rows -->
<StackPanel Spacing="10">
    <!-- Realmlist Address -->
    <StackPanel Visibility="{Binding HasRealmlistAddress}">
        <TextBlock Text="Realmlist" 
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="10"
                   Foreground="{StaticResource TextMutedBrush}"/>
        <TextBlock Text="{Binding RealmlistAddress}" 
                   FontFamily="{StaticResource SerifFont}"
                   FontSize="12"
                   FontStyle="Italic"
                   Foreground="{StaticResource TextPrimaryBrush}"
                   TextWrapping="Wrap"/>
    </StackPanel>

    <!-- Account Name -->
    <StackPanel Visibility="{Binding HasAccountName}">
        <TextBlock Text="Account" 
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="10"
                   Foreground="{StaticResource TextMutedBrush}"/>
        <TextBlock Text="{Binding AccountName}" 
                   FontFamily="{StaticResource SerifFont}"
                   FontSize="12"
                   FontStyle="Italic"
                   Foreground="{StaticResource TextPrimaryBrush}"/>
    </StackPanel>

    <!-- Realm Name -->
    <StackPanel Visibility="{Binding HasRealmName}">
        <TextBlock Text="Realm" 
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="10"
                   Foreground="{StaticResource TextMutedBrush}"/>
        <TextBlock Text="{Binding RealmName}" 
                   FontFamily="{StaticResource SerifFont}"
                   FontSize="12"
                   FontStyle="Italic"
                   Foreground="{StaticResource TextPrimaryBrush}"/>
    </StackPanel>
</StackPanel>
```

**Automation Settings Section:**
```xml
<!-- Section Header -->
<TextBlock Text="⚙ AUTOMATION" 
           FontFamily="{StaticResource DisplayFont}"
           FontSize="9"
           CharacterSpacing="150"
           Foreground="{StaticResource GoldDarkColor}"
           Margin="0,20,0,12"/>

<StackPanel Spacing="10">
    <!-- Password Input Method -->
    <StackPanel>
        <TextBlock Text="Password Method" 
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="10"
                   Foreground="{StaticResource TextMutedBrush}"/>
        <TextBlock FontFamily="{StaticResource SerifFont}"
                   FontSize="12"
                   FontStyle="Italic"
                   Foreground="{StaticResource TextPrimaryBrush}">
            <Run Text="{Binding InputMethod}"/>
        </TextBlock>
    </StackPanel>

    <!-- Window Title (if set) -->
    <StackPanel Visibility="{Binding HasWindowTitle}">
        <TextBlock Text="Window Title" 
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="10"
                   Foreground="{StaticResource TextMutedBrush}"/>
        <TextBlock Text="{Binding WindowTitle}" 
                   FontFamily="{StaticResource SerifFont}"
                   FontSize="12"
                   FontStyle="Italic"
                   Foreground="{StaticResource TextPrimaryBrush}"/>
    </StackPanel>
</StackPanel>
```

**Launch Settings Section:**
```xml
<!-- Section Header -->
<TextBlock Text="🗡 LAUNCH" 
           FontFamily="{StaticResource DisplayFont}"
           FontSize="9"
           CharacterSpacing="150"
           Foreground="{StaticResource GoldDarkColor}"
           Margin="0,20,0,12"/>

<StackPanel Spacing="10">
    <!-- Executable Path -->
    <StackPanel>
        <TextBlock Text="Executable" 
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="10"
                   Foreground="{StaticResource TextMutedBrush}"/>
        <TextBlock Text="{Binding ExecutablePath}" 
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="11"
                   Foreground="{StaticResource TextPrimaryBrush}"
                   TextWrapping="Wrap"/>
    </StackPanel>
</StackPanel>
```

#### Action Buttons (Bottom)
```xml
<StackPanel Orientation="Horizontal" 
            Spacing="12"
            Margin="0,32,0,0">

    <!-- Edit Button -->
    <Button Content="Edit Settings"
            Style="{StaticResource SecondaryButtonStyle}"
            Height="40"
            HorizontalAlignment="Stretch"
            Grid.Column="0"/>

    <!-- Launch Button -->
    <Button Content="⚔ Launch"
            Style="{StaticResource PrimaryButtonStyle}"
            Height="40"
            HorizontalAlignment="Stretch"
            Grid.Column="1"/>
</StackPanel>
```

**Animation:**
- Slide in: `TranslateTransform.X` from `400` to `0` with `Duration=0:0:0.3`, `EasingFunction=CubicEase Out`
- Slide out: `TranslateTransform.X` from `0` to `400` with `Duration=0:0:0.25`, `EasingFunction=CubicEase In`

**Visual Polish:**
- Add subtle `DropShadowEffect` to entire panel: `Color=#000000`, `BlurRadius=32`, `Opacity=0.4`
- Optional: Background pattern overlay using `pattern-noise.png` at 3% opacity

### 4. Button Visual Styles

**Primary button (Launch):**
- Background: Emerald gradient
- Border: EmeraldLight 30%
- Foreground: `#C8F0D8`
- Height: 50px
- Font: Cinzel Bold
- Optional shimmer animation: white `Rectangle` (`Opacity=0.06`) that translates X from -100% to 200% with `Duration=0:0:3`, `RepeatBehavior=Forever`

**Secondary button (Edit, Cancel, etc.):**
- Background: Transparent
- Border: 1px `{StaticResource BorderHighColor}`
- Foreground: `{StaticResource GoldBrush}`
- Height: 40px
- Font: Cinzel Regular
- Hover: Border becomes `{StaticResource GoldBrush}`, Background gains gold 5% opacity

**Slider (Gold):**
```xml
<Style x:Key="GoldSliderStyle" TargetType="Slider">
    <!-- Thumb: 8px circle, GoldLight fill, GoldDark border -->
    <!-- Fill portion: GoldDark brush -->
    <!-- Track: White 7% -->
</Style>
```

---

## Animation Guidelines

### Slide In/Out (Panels, Dialogs)
```xml
<Storyboard x:Name="SlideInAnimation">
    <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.X)"
                     From="400" To="0"
                     Duration="0:0:0.3">
        <DoubleAnimation.EasingFunction>
            <CubicEase EasingMode="EaseOut"/>
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>

    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                     From="0" To="1"
                     Duration="0:0:0.2"/>
</Storyboard>
```

### Fade In/Out
```xml
<Storyboard x:Name="FadeInAnimation">
    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                     From="0" To="1"
                     Duration="0:0:0.2"/>
</Storyboard>
```

### Scale (Button Press)
```xml
<Storyboard x:Name="ScaleDownAnimation">
    <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleX)"
                     To="0.96" Duration="0:0:0.1"/>
    <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleY)"
                     To="0.96" Duration="0:0:0.1"/>
</Storyboard>
```

---

## Accessibility Requirements

- **Keyboard Navigation:** All interactive elements must be keyboard accessible
- **Focus Indicators:** Clear 2px gold outline for keyboard focus
- **Screen Reader:** Use `AutomationProperties.Name` on all controls
- **Color Contrast:** Maintain 4.5:1 ratio for text (already met with current palette)
- **Touch Targets:** Minimum 44px height for all interactive elements

