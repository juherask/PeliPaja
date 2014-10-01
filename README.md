PeliPaja
========

== Johdanto ==
Pelipajan tarkoitus on tutustuttaa osallistujat pelien tekemisen eri osa-alueisiin yhteisen tietokonepeliprojektin avulla. Työpajassa voi joko piirtää peliin omia pelihahmoja tai kerättäviä esineitä, suunnitella peliin karttoja, säveltää musiikkia tai vaikka ohjelmoida - sen mukaan mikä pelin tekemisen osa-alue eniten kiinnostaa. Peliä voi myös koko ajan pelata, joten pelipajassa pääsee kokeilemaan, pystyykö pelaamaan kaverin (tai itsensä!)
suunnitteleman kentän läpi.

== Valmistelut ==
Pelipajassa tulee olla riittävästi tietokoneita. Koska osallistujat voivat vuorotella tai tehdä sisältöjä yhdessä saman koneen ääressä, jokaiselle ei tarvitse kuitenkaan olla omaa konetta. Koneisiin on asennettu [Jypeli](https://trac.cc.jyu.fi/projects/npo/wiki/KurssiSoftat), [Paint.NET](http://www.getpaint.net/download.html#download) ja [TortoiseSVN](). Huomaa, että TortoiseSVN:stä pitää valita asennettavaksi myös komentorivityökalut. Lisäksi tarvitset jokaiselle koneelle tästä Github-säiliöstä löytyvän asiakasohjelman nimeltään WorkshopClient ja muutaman muun työkalun joita ei sen kummemmin tarvitse asentaa - riittää että niistä on kopiot koneella, esim. `C:\Temp\` -kansiossa. Lisäksi koneissa tulee olla internetyhteys. Mikäli palomuuri estää SVN protokollan, aseta myös HTTP-proxy TortoiseSVNn asetuksista.

Pelipajassa syntyvän **Pajapelin** lähdekoodit ja sisällöt ovat Subversion-versionhallintapalvelimella. Koska Github tukee SVN protokollaa, on suositeltavaa käyttää sitä Pelipajassa:
* Luo uusi Github-tunnus pelipajan käyttöön.
* Kirjaudu sisään Githubin weppisivuille tällä uudella tunnuksella. 
* [Forkkaa](https://help.github.com/articles/fork-a-repo) Pajapelin Github-säiliö osoitteesta: [I'm an inline-style link](https://www.google.com)



TODO: Fork PajaPeli

Valmistelleksasi työpisteen:
* Kopioi kaikki kansiosta (TODO: tee installeri/binääri ja laita se jonnekin ladattavaksi) kansio C:/Temp/ (tarkista, että kansiossa on ajo-oikeudet).
* Aja sfxr.exe
 * vie (export) yksi sämple kansioon "PajaPeli/DynamicContent/Tehosteet/". Tämä asettaa oletustallennussijainnin.
 * Poista sämple.wav
- Aja milkytracker.exe
 * tallenna ([Disk.op.], [*] wav, [Save as], [As..] ) tyhjä biisi kansioon "PajaPeli/DynamicContent/Musiikki/". Tämä asettaa oletustallennussijainnin.
 * Poista biisi.wav
- Commitoi kerran TortoiseSVN:llä joku pikkumuutos GitHubiin ja ruksi, "tallenna salasana". Käytä tunnusta ja salasanaa, jolla forkkasit PajaPeli -Github säiliön yllä.
- Käynnistä työpajan asiakasohjelma WorkshopClient.exe

== Kuvaus ==

Pelipajan osallistuja tekee peliä allakuvatun asiakasohjelman käyttöliittymän kautta. 

* Painamalla "Pelaa uusinta versiota pelistä" osallistuja voi pelata PeliPajassa syntyvää peliä. Pellin alla tapahtuu seuraavaa: asikasohjelma hakee versionhallinnasta (SVN) PajaPelin viimeisimmät lähdekoodi- ja sisältötiedostot, kääntää pelin käyttäen "msbuild.exe"-työkalua ja käynnistää syntyneen pelibinäärin (Pajapeli.exe).

* Painamalla "Tee uusi pelihahmo peliin" peliin lisätään uusi pelaajahahmo tai vihollinen. Kun nappia on painettu, aukeaa Paint.Net ohjelmassa tyhjä 50x50 kokoinen kuva, jonka vasemman yläkulman pikselin oletusväri määrää että kyseessä on pelaajahahmo. Jos kyiseisen pikselin värin vaihtaa Paint.Netin paletin äärimmäisen oikean yläkulman fuksianpunaiseksi, tulkitaan piirretty hahmo viholliseksi. Käyttämällä mitä tahansa muuta väriä, voi tehdä erikoistuneita vihollisia, mutta tällöin kyseisen värisen pikselin pitää esiintyä jossain kentässä/kartassa tai vihollinen ei ilmaannu (katso "Tee uusi kartta peliin"). Tehtyä hahmoa ei tallenneta nimellä, vaan tallennetaan nimeämättä (CTRL+S), jonka jälkeen Paint.Netin voi sulkea ruksista, jonka jälkeen asiakasohjelma palaa ruudulle. Asiakasohjelma pyytää vielä nimeämään hahmon, jonka jälkeen painamalla "Pelaa uusinta versiota pelistä" voi ihastella omaa tekelettään pelissä. Jos on niin tyytyväinen taiteeseensa, että haluaa laittaa hahmon muidenkin peleihin, tulee painaa "Lisää tekemäsi sisältö peliin". Tästä toiminnosta lisää alempana.

== Kysymyksiä ja Vastauksia ==

Q: Tarvitseeko Pelipajan koneille todella asentaa Visual C# (Express).
V: Ei välttämättä, jos pajassa ei ole tarkoitus koodata. Tällöin riittää, että asentaa [XNA4.0 redistributablen](http://www.microsoft.com/en-us/download/details.aspx?id=20914) ja [Jypelin sen asennusohjelmalla]()