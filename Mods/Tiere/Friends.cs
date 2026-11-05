// GC_Tierarten und GC_WalkuerenTransport bauen auf GC_Tiere auf (harte Abhängigkeit) und nutzen seine internen Typen.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GC_Tierarten")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GC_WalkuerenTransport")]
// GC_Outfit nutzt die Leinenleiste als Partner (Sätze am Kleiderschrank), ohne GC_Tiere läuft es weiter.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GC_Outfit")]
