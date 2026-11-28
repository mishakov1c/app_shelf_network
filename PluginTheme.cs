// Standalone styles adapted from AppShelf beta.4, 9daab59a. No host dependency.
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace AppShelf {
 internal static class PluginTheme {
  private static BitmapFrame windowIcon;
  private static BitmapFrame WindowIcon() {
   if(windowIcon == null) {
    using(var stream=typeof(PluginTheme).Assembly.GetManifestResourceStream("AppShelf.Network.Icon.ico")) {
     if(stream == null) throw new InvalidOperationException("The embedded NetworkPlugin icon is missing.");
     windowIcon=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
     windowIcon.Freeze();
    }
   }
   return windowIcon;
  }
 internal static readonly Brush Background = Brush("#0E1420"), Surface = Brush("#192334"), SurfaceRaised = Brush("#222E43"), SurfaceHover = Brush("#30415C"), BorderColor = Brush("#34445B"), Foreground = Brush("#F4F7FD"), Secondary = Brush("#AABAD0"), Accent = Brush("#88B8FF");
 private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
 [DllImport("dwmapi.dll", PreserveSig=true)] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
 internal static void Prepare(Window window) {
  window.Background=Background; window.Foreground=Foreground; window.FontFamily=new FontFamily("Segoe UI"); window.FontSize=13;
  window.Icon=WindowIcon();
  window.Resources.MergedDictionaries.Add(SettingsScrollResources()); window.Resources.MergedDictionaries.Add(SelectionResources());
  window.Resources[SystemColors.ControlBrushKey]=Brush("#151D2C"); window.Resources[typeof(Button)]=ButtonStyle(); window.Resources[typeof(TextBox)]=TextBoxStyle(); window.Resources[typeof(CheckBox)]=CheckBoxStyle();
  window.SourceInitialized += delegate { try { var hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle; int enabled=1; if(DwmSetWindowAttribute(hwnd,20,ref enabled,4)!=0) DwmSetWindowAttribute(hwnd,19,ref enabled,4); } catch(DllNotFoundException) {} catch(EntryPointNotFoundException) {} };
 }
 internal static Button Button(string caption,bool primary=false) { var button=new Button { Content=caption, Margin=new Thickness(0,0,8,8) }; if(primary) { button.Background=Accent; button.Foreground=Background; button.BorderBrush=Accent; } return button; }
 internal static TextBlock Label(string caption,double size,bool muted=false) { return new TextBlock { Text=caption,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=muted?Secondary:Foreground }; }
 internal static TextBox Report() { return new TextBox { IsReadOnly=true,FontFamily=new FontFamily("Consolas"),FontSize=12.5,Padding=new Thickness(12),TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto }; }
        private static ControlTemplate ButtonTemplate(bool stretchContent = false)
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.BackgroundProperty,
                new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty,
                new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty,
                new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                stretchContent ? HorizontalAlignment.Stretch : HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(content);
            template.VisualTree = border;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Chrome"));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.OpacityProperty, 0.77, "Chrome"));
            template.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.OpacityProperty, 0.45, "Chrome"));
            template.Triggers.Add(disabled);
            return template;
        }

        private static Style ButtonStyle()
        {
            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceRaised));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderColor));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13, 8, 13, 8)));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34.0));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, ButtonTemplate()));
            return style;
        }

        private static ControlTemplate TextBoxTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "InputChrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetBinding(Border.BorderThicknessProperty,
                new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BackgroundProperty,
                new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty,
                new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            FrameworkElementFactory host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            // TextBoxView already applies TextBox.Padding. A second margin clips the glyphs.
            host.SetBinding(Control.VerticalContentAlignmentProperty,
                new Binding("VerticalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(host);
            template.VisualTree = border;
            Trigger focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "InputChrome"));
            template.Triggers.Add(focused);
            return template;
        }

        private static Style TextBoxStyle()
        {
            Style style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderColor));
            style.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Accent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
            style.Setters.Add(new Setter(Control.TemplateProperty, TextBoxTemplate()));
            return style;
        }

        private static Style CheckBoxStyle(bool readableDisabled = false)
        {
            ControlTemplate template = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory line = new FrameworkElementFactory(typeof(DockPanel));
            line.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "CheckFrame";
            frame.SetValue(DockPanel.DockProperty, Dock.Left);
            frame.SetValue(FrameworkElement.WidthProperty, 18.0);
            frame.SetValue(FrameworkElement.HeightProperty, 18.0);
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            frame.SetValue(Border.BackgroundProperty, Surface);
            frame.SetValue(Border.BorderBrushProperty, BorderColor);
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            FrameworkElementFactory tick = new FrameworkElementFactory(typeof(TextBlock));
            tick.Name = "CheckTick";
            tick.SetValue(TextBlock.TextProperty, "✓");
            tick.SetValue(TextBlock.FontSizeProperty, 13.0);
            tick.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            tick.SetValue(TextBlock.ForegroundProperty, Accent);
            tick.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            tick.SetValue(FrameworkElement.VisibilityProperty, Visibility.Collapsed);
            frame.AppendChild(tick);
            line.AppendChild(frame);
            FrameworkElementFactory label = new FrameworkElementFactory(typeof(ContentPresenter));
            label.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            label.SetBinding(ContentPresenter.ContentProperty,
                new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            line.AppendChild(label);
            template.VisualTree = line;
            Trigger checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckTick"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "CheckFrame"));
            template.Triggers.Add(checkedTrigger);
            Trigger disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(readableDisabled
                ? new Setter(Control.ForegroundProperty, Secondary)
                : new Setter(UIElement.OpacityProperty, 0.5));
            template.Triggers.Add(disabledTrigger);
            Style style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        // These resources are opt-in: do not replace controls in other windows.
        // Dedicated slider used only by the appearance settings. The colored part of
        // the native WPF Track reflects the value; keyboard/mouse/drag work normally.
        private static ResourceDictionary SettingsScrollResources()
        {
            // Scoped to settings/menu resources. Built-in ScrollViewer keeps wheel, keys,
            // touchpad gestures and keyboard focus handling.
            ResourceDictionary resources = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 xmlns:sys='clr-namespace:System;assembly=mscorlib'
 xmlns:p='clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework'>
 <!-- The system ScrollViewer template sets its own dynamic Width on the bar. -->
 <sys:Double x:Key='{x:Static SystemParameters.VerticalScrollBarWidthKey}'>12</sys:Double>
 <sys:Double x:Key='{x:Static SystemParameters.HorizontalScrollBarHeightKey}'>12</sys:Double>
 <Style TargetType='ScrollBar'>
  <Setter Property='Width' Value='12'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
   <Border Background='#151D2C' CornerRadius='6'>
    <p:Track x:Name='PART_Track' IsDirectionReversed='True'
      Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
      Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
     <p:Track.DecreaseRepeatButton>
      <RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' Opacity='0'/>
     </p:Track.DecreaseRepeatButton>
     <p:Track.Thumb><Thumb>
      <Thumb.Template><ControlTemplate TargetType='Thumb'>
       <Border x:Name='Grip' Background='#607590' CornerRadius='5' Margin='2,2'/>
       <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'>
         <Setter TargetName='Grip' Property='Background' Value='#88B8FF'/>
        </Trigger>
        <Trigger Property='IsDragging' Value='True'>
         <Setter TargetName='Grip' Property='Background' Value='#B7D3FF'/>
        </Trigger>
       </ControlTemplate.Triggers>
      </ControlTemplate></Thumb.Template>
     </Thumb></p:Track.Thumb>
     <p:Track.IncreaseRepeatButton>
      <RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' Opacity='0'/>
     </p:Track.IncreaseRepeatButton>
    </p:Track>
   </Border>
  </ControlTemplate></Setter.Value></Setter>
  <Style.Triggers>
   <Trigger Property='Orientation' Value='Horizontal'>
    <Setter Property='Width' Value='Auto'/>
    <Setter Property='Height' Value='12'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
     <Border Background='#151D2C' CornerRadius='6'>
      <p:Track x:Name='PART_Track' Orientation='Horizontal'
       Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
       Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
       <p:Track.DecreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageLeftCommand' Focusable='False' Opacity='0'/>
       </p:Track.DecreaseRepeatButton>
       <p:Track.Thumb><Thumb>
        <Thumb.Template><ControlTemplate TargetType='Thumb'>
         <Border x:Name='Grip' Background='#607590' CornerRadius='5' Margin='2,2'/>
         <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
           <Setter TargetName='Grip' Property='Background' Value='#88B8FF'/>
          </Trigger>
          <Trigger Property='IsDragging' Value='True'>
           <Setter TargetName='Grip' Property='Background' Value='#B7D3FF'/>
          </Trigger>
         </ControlTemplate.Triggers>
        </ControlTemplate></Thumb.Template>
       </Thumb></p:Track.Thumb>
       <p:Track.IncreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageRightCommand' Focusable='False' Opacity='0'/>
       </p:Track.IncreaseRepeatButton>
      </p:Track>
     </Border>
    </ControlTemplate></Setter.Value></Setter>
   </Trigger>
  </Style.Triggers>
 </Style>
</ResourceDictionary>");
            return resources;
        }

        private static ResourceDictionary SelectionResources()
        {
            ResourceDictionary resources = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 xmlns:p='clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework'>
 <Style TargetType='ComboBoxItem'>
  <Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='Background' Value='#192334'/>
  <Setter Property='Padding' Value='10,7'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'>
   <Border x:Name='Chrome' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'>
    <ContentPresenter VerticalAlignment='Center'/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Chrome' Property='Opacity' Value='0.55'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ComboBox'>
  <Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='BorderBrush' Value='#34445B'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'>
   <Grid>
    <ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
     <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>
      <Border x:Name='Chrome' Background='#192334' BorderBrush='{Binding BorderBrush, RelativeSource={RelativeSource AncestorType=ComboBox}}' BorderThickness='1' CornerRadius='8'>
       <TextBlock Text='&#x25BE;' Foreground='#AABAD0' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,10,0'/>
      </Border><ControlTemplate.Triggers>
       <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
      </ControlTemplate.Triggers>
     </ControlTemplate></ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
     Margin='10,4,30,4' VerticalAlignment='Center' IsHitTestVisible='False'/>
    <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'>
     <Border Background='#192334' BorderBrush='#34445B' BorderThickness='1' CornerRadius='6'
      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
      <ScrollViewer MaxHeight='{TemplateBinding MaxDropDownHeight}' CanContentScroll='True'
       HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'><ItemsPresenter/></ScrollViewer>
     </Border>
    </Popup>
   </Grid><ControlTemplate.Triggers>
    <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.55'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ListBoxItem'>
  <Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='Background' Value='#192334'/>
  <Setter Property='Padding' Value='7,4'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'>
   <Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='Transparent' BorderThickness='1' CornerRadius='4' Padding='{TemplateBinding Padding}'>
    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#222E43'/></Trigger>
    <Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='#AABAD0'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ListBox'>
  <Setter Property='Background' Value='#192334'/><Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='BorderBrush' Value='#34445B'/><Setter Property='BorderThickness' Value='1'/>
  <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Disabled'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBox'>
   <Border x:Name='ListChrome' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' Padding='3'>
    <ScrollViewer Focusable='False' CanContentScroll='True' HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'><ItemsPresenter/></ScrollViewer>
   </Border>
   <ControlTemplate.Triggers>
    <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='ListChrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='ListChrome' Property='Opacity' Value='0.55'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>");
            return resources;
        }

 }
}
