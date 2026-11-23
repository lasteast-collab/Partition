S = {
# ---- Findings
"Finding.NotElevated": ("Running without administrator rights. Detection works for most devices; editing features (later phases) will request elevation.","פועל ללא הרשאות מנהל. הזיהוי עובד ברוב ההתקנים; תכונות עריכה (בשלבים הבאים) יבקשו הרשאות מנהל.","يعمل دون صلاحيات المسؤول. يعمل الاكتشاف مع معظم الأجهزة؛ وستطلب ميزات التعديل (في المراحل اللاحقة) رفع الصلاحيات.","Exécution sans droits d’administrateur. La détection fonctionne pour la plupart des périphériques ; les fonctions de modification (phases suivantes) demanderont l’élévation.","Ejecutando sin derechos de administrador. La detección funciona en la mayoría de dispositivos; las funciones de edición (fases posteriores) pedirán elevación.","Ausführung ohne Administratorrechte. Die Erkennung funktioniert bei den meisten Geräten; Bearbeitungsfunktionen (spätere Phasen) fordern Rechte an.","Запуск без прав администратора. Обнаружение работает для большинства устройств; функции изменения (следующие этапы) запросят повышение прав."),
"Finding.LowSpace": ("Low free space on {0}: {1} free ({2} of capacity).","מקום פנוי נמוך ב-{0}: ‏{1} פנויים ({2} מהקיבולת).","مساحة حرة منخفضة على {0}: ‏{1} متاحة ({2} من السعة).","Espace libre faible sur {0} : {1} libres ({2} de la capacité).","Poco espacio libre en {0}: {1} libres ({2} de la capacidad).","Wenig freier Speicher auf {0}: {1} frei ({2} der Kapazität).","Мало свободного места на {0}: свободно {1} ({2} ёмкости)."),
"Finding.Misaligned": ("Disk {0}, partition {1} does not start on a 4 KiB boundary, which can reduce performance on SSD and Advanced Format disks.","דיסק {0}, מחיצה {1} אינה מתחילה בגבול של 4 KiB, דבר שעלול להפחית ביצועים ב-SSD ובדיסקי Advanced Format.","القرص {0}، القسم {1} لا يبدأ عند حد 4 KiB، مما قد يقلل الأداء على أقراص SSD وAdvanced Format.","Disque {0}, partition {1} : le début n’est pas aligné sur 4 Kio, ce qui peut réduire les performances des SSD et disques Advanced Format.","Disco {0}, partición {1}: no comienza en un límite de 4 KiB, lo que puede reducir el rendimiento en SSD y discos Advanced Format.","Datenträger {0}, Partition {1} beginnt nicht an einer 4-KiB-Grenze; das kann die Leistung von SSDs und Advanced-Format-Laufwerken mindern.","Диск {0}, раздел {1} не выровнен по границе 4 КиБ; это может снизить производительность SSD и дисков Advanced Format."),
"Finding.VolumeUnreadable": ("Volume {0} could not be read. It may be locked by BitLocker, RAW, or have no media.","לא ניתן לקרוא את אמצעי האחסון {0}. ייתכן שהוא נעול על ידי BitLocker, במצב RAW או ללא מדיה.","تعذّرت قراءة وحدة التخزين {0}. قد تكون مقفلة بواسطة BitLocker أو بتنسيق RAW أو بلا وسائط.","Le volume {0} n’a pas pu être lu. Il est peut-être verrouillé par BitLocker, RAW ou sans support.","No se pudo leer el volumen {0}. Puede estar bloqueado por BitLocker, ser RAW o no tener medio.","Volume {0} konnte nicht gelesen werden. Es ist evtl. durch BitLocker gesperrt, RAW oder ohne Medium.","Не удалось прочитать том {0}. Возможно, он заблокирован BitLocker, имеет формат RAW или нет носителя."),
"Finding.LayoutProblem": ("Disk {0}: the partition table has {1} inconsistency(ies). Do not modify this disk until it has been investigated.","דיסק {0}: בטבלת המחיצות יש {1} חוסרי עקביות. אין לשנות דיסק זה עד שייבדק.","القرص {0}: يحتوي جدول الأقسام على {1} حالة/حالات عدم اتساق. لا تعدّل هذا القرص قبل فحصه.","Disque {0} : la table de partitions présente {1} incohérence(s). Ne modifiez pas ce disque avant analyse.","Disco {0}: la tabla de particiones tiene {1} incoherencia(s). No modifiques este disco hasta investigarlo.","Datenträger {0}: Die Partitionstabelle weist {1} Inkonsistenz(en) auf. Ändern Sie ihn erst nach einer Prüfung.","Диск {0}: в таблице разделов несоответствий: {1}. Не изменяйте диск до выяснения причин."),
"Finding.Unallocated": ("Disk {0} has {1} of unallocated space.","בדיסק {0} יש {1} של שטח לא מוקצה.","يحتوي القرص {0} على {1} من المساحة غير المخصصة.","Le disque {0} contient {1} d’espace non alloué.","El disco {0} tiene {1} de espacio sin asignar.","Datenträger {0} hat {1} nicht zugeordneten Speicher.","На диске {0} не распределено {1}."),
"Finding.Mbr2TB": ("Disk {0} uses MBR but is larger than 2 TB; MBR cannot address space beyond 2 TB.","דיסק {0} משתמש ב-MBR אך גדול מ-2 TB; ‏MBR אינו יכול לנצל שטח מעבר ל-2 TB.","يستخدم القرص {0} نظام MBR لكنه أكبر من 2 TB؛ لا يستطيع MBR الوصول إلى المساحة بعد 2 TB.","Le disque {0} utilise MBR mais dépasse 2 To ; MBR ne peut pas adresser l’espace au-delà de 2 To.","El disco {0} usa MBR pero supera 2 TB; MBR no puede usar el espacio más allá de 2 TB.","Datenträger {0} nutzt MBR, ist aber größer als 2 TB; MBR kann Speicher über 2 TB nicht adressieren.","Диск {0} использует MBR, но больше 2 ТБ; MBR не может адресовать пространство свыше 2 ТБ."),
"Finding.Dynamic": ("Disk {0} is a dynamic disk. Lasteast shows it for information only.","דיסק {0} הוא דיסק דינמי. Lasteast מציג אותו לצורכי מידע בלבד.","القرص {0} قرص ديناميكي. يعرضه Lasteast للمعلومات فقط.","Le disque {0} est dynamique. Lasteast l’affiche à titre d’information uniquement.","El disco {0} es dinámico. Lasteast lo muestra solo a título informativo.","Datenträger {0} ist dynamisch. Lasteast zeigt ihn nur zur Information an.","Диск {0} динамический. Lasteast показывает его только для информации."),
"Finding.TrimOff": ("TRIM is not enabled for SSD disk {0}.","TRIM אינו מופעל עבור דיסק ה-SSD ‏{0}.","TRIM غير مفعّل لقرص SSD رقم {0}.","TRIM n’est pas activé pour le disque SSD {0}.","TRIM no está activado en el disco SSD {0}.","TRIM ist für SSD-Datenträger {0} nicht aktiviert.","TRIM не включён для SSD-диска {0}."),
# ---- Severity
"Severity.Critical": ("Critical","קריטי","حرج","Critique","Crítico","Kritisch","Критично"),
"Severity.High": ("High","גבוה","مرتفع","Élevé","Alto","Hoch","Высокий"),
"Severity.Recommended": ("Recommended","מומלץ","موصى به","Recommandé","Recomendado","Empfohlen","Рекомендуется"),
"Severity.Optional": ("Optional","אופציונלי","اختياري","Facultatif","Opcional","Optional","Необязательно"),
"Severity.Info": ("Info","מידע","معلومات","Info","Info","Info","Инфо"),
# ---- Partition table styles
"Style.Unknown": ("Unknown","לא ידוע","غير معروف","Inconnu","Desconocido","Unbekannt","Неизвестно"),
"Style.Mbr": ("MBR","MBR","MBR","MBR","MBR","MBR","MBR"),
"Style.Gpt": ("GPT","GPT","GPT","GPT","GPT","GPT","GPT"),
"Style.Raw": ("Not initialized","לא מאותחל","غير مهيأ","Non initialisé","Sin inicializar","Nicht initialisiert","Не инициализирован"),
# ---- Media
"Media.Unknown": ("Unknown media","מדיה לא ידועה","وسائط غير معروفة","Support inconnu","Medio desconocido","Unbekanntes Medium","Неизвестный носитель"),
"Media.Hdd": ("HDD","HDD","HDD","HDD","HDD","HDD","HDD"),
"Media.Ssd": ("SSD","SSD","SSD","SSD","SSD","SSD","SSD"),
# ---- Alignment
"Align.Aligned1MiB": ("Aligned (1 MiB)","מיושר (1 MiB)","محاذى (1 MiB)","Aligné (1 Mio)","Alineado (1 MiB)","Ausgerichtet (1 MiB)","Выровнен (1 МиБ)"),
"Align.AlignedPhysical": ("Aligned (4 KiB)","מיושר (4 KiB)","محاذى (4 KiB)","Aligné (4 Kio)","Alineado (4 KiB)","Ausgerichtet (4 KiB)","Выровнен (4 КиБ)"),
"Align.Misaligned": ("Not aligned","לא מיושר","غير محاذى","Non aligné","No alineado","Nicht ausgerichtet","Не выровнен"),
# ---- Firmware
"Firmware.Unknown": ("Firmware: unknown","קושחה: לא ידוע","البرنامج الثابت: غير معروف","Micrologiciel : inconnu","Firmware: desconocido","Firmware: unbekannt","Прошивка: неизвестно"),
"Firmware.Bios": ("Legacy BIOS","BIOS מסורתי","BIOS قديم","BIOS hérité","BIOS heredado","Legacy-BIOS","Legacy BIOS"),
"Firmware.Uefi": ("UEFI","UEFI","UEFI","UEFI","UEFI","UEFI","UEFI"),
# ---- Units
"Unit.B": ("B","B","بايت","o","B","B","Б"),
"Unit.KB": ("KB","KB","ك.ب","Ko","KB","KB","КБ"),
"Unit.MB": ("MB","MB","م.ب","Mo","MB","MB","МБ"),
"Unit.GB": ("GB","GB","ج.ب","Go","GB","GB","ГБ"),
"Unit.TB": ("TB","TB","ت.ب","To","TB","TB","ТБ"),
"Unit.PB": ("PB","PB","ب.ب","Po","PB","PB","ПБ"),
}

# Partition roles
ROLES = {
"Unknown": ("Unknown","לא ידוע","غير معروف","Inconnu","Desconocido","Unbekannt","Неизвестно"),
"BasicData": ("Basic data","נתונים בסיסיים","بيانات أساسية","Données de base","Datos básicos","Basisdaten","Основной раздел данных"),
"EfiSystem": ("EFI system","מערכת EFI","نظام EFI","Système EFI","Sistema EFI","EFI-System","Системный EFI"),
"MicrosoftReserved": ("Microsoft reserved","שמור Microsoft","محجوز لـ Microsoft","Réservé Microsoft","Reservado de Microsoft","Microsoft-reserviert","Зарезервировано Microsoft"),
"Recovery": ("Recovery","שחזור","الاسترداد","Récupération","Recuperación","Wiederherstellung","Восстановление"),
"MbrExtended": ("Extended","מורחבת","ممتد","Étendue","Extendida","Erweitert","Расширенный"),
"MbrLogical": ("Logical","לוגית","منطقي","Logique","Lógica","Logisch","Логический"),
"LdmMetadata": ("LDM metadata","מטא-נתונים LDM","بيانات تعريف LDM","Métadonnées LDM","Metadatos LDM","LDM-Metadaten","Метаданные LDM"),
"LdmData": ("Dynamic (LDM) data","נתונים דינמיים (LDM)","بيانات ديناميكية (LDM)","Données dynamiques (LDM)","Datos dinámicos (LDM)","Dynamische Daten (LDM)","Динамические данные (LDM)"),
"StorageSpaces": ("Storage Spaces","Storage Spaces","مساحات التخزين","Espaces de stockage","Espacios de almacenamiento","Speicherplätze","Дисковые пространства"),
"Linux": ("Linux","Linux","Linux","Linux","Linux","Linux","Linux"),
"LinuxSwap": ("Linux swap","החלפה של Linux","مبادلة Linux","Swap Linux","Intercambio de Linux","Linux-Swap","Подкачка Linux"),
"AppleHfs": ("Apple","Apple","Apple","Apple","Apple","Apple","Apple"),
"Other": ("Other","אחר","أخرى","Autre","Otro","Sonstige","Другой"),
}

BUS = {"Unknown":("Unknown bus","חיבור לא ידוע","ناقل غير معروف","Bus inconnu","Bus desconocido","Unbekannter Bus","Неизвестная шина")}
for k,v in {"Scsi":"SCSI","Atapi":"ATAPI","Ata":"ATA","Ieee1394":"IEEE 1394","Ssa":"SSA","Fibre":"Fibre Channel","Usb":"USB","Raid":"RAID",
            "IScsi":"iSCSI","Sas":"SAS","Sata":"SATA","Sd":"SD","Mmc":"MMC","Spaces":"Storage Spaces","Nvme":"NVMe","Scm":"SCM","Ufs":"UFS"}.items():
    BUS[k]=(v,)*7
BUS["Virtual"]=("Virtual","וירטואלי","افتراضي","Virtuel","Virtual","Virtuell","Виртуальная")
BUS["FileBackedVirtual"]=("Virtual disk file (VHD/VHDX)","קובץ דיסק וירטואלי (VHD/VHDX)","ملف قرص افتراضي (VHD/VHDX)","Fichier de disque virtuel (VHD/VHDX)","Archivo de disco virtual (VHD/VHDX)","Virtuelle Datenträgerdatei (VHD/VHDX)","Файл виртуального диска (VHD/VHDX)")
