// Centralized using directives for WinGet Scout. Keeps every other file lean and
// removes repetition. The Attribute alias resolves the ambiguity between System.Attribute
// (almost never wanted) and Terminal.Gui.Drawing.Attribute (what we mean every time).

global using System.Collections.Generic;
global using System.Collections.ObjectModel;
global using System.Diagnostics;
global using System.IO;
global using System.Linq;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;

global using Terminal.Gui.App;
global using Terminal.Gui.Configuration;
global using Terminal.Gui.Drawing;
global using Terminal.Gui.Drivers;
global using Terminal.Gui.Input;
global using Terminal.Gui.ViewBase;
global using Terminal.Gui.Views;
global using Terminal.Gui.Text;

global using Attribute = Terminal.Gui.Drawing.Attribute;
