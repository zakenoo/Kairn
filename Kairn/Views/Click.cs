using System.Windows;

namespace Kairn.Views;

/// <summary>
/// Clic fiable sur un élément qui n'est pas un bouton (carte, vignette, case du calendrier…) :
/// l'action ne se déclenche que si le bouton de la souris a été enfoncé ET relâché sur le même élément.
/// Sans ça, relâcher la souris au-dessus d'une vignette après avoir fait glisser la barre de défilement l'activerait.
/// </summary>
public static class Click
{
    private static object? _pressed;

    public static void Down(object sender) => _pressed = sender;

    public static bool Up(object sender)
    {
        bool ok = ReferenceEquals(_pressed, sender);
        _pressed = null;
        return ok;
    }

    public static void Attach(UIElement element, Action action)
    {
        element.MouseLeftButtonDown += (s, _) => Down(s);
        element.MouseLeftButtonUp += (s, _) => { if (Up(s)) action(); };
    }
}
