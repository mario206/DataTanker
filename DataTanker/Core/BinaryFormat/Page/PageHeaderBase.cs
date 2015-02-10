namespace DataTanker.BinaryFormat.Page
{
    using System;
    using PageManagement;
    using MemoryManagement;

    internal abstract class PageHeaderBase
    {
        private short _length = 4;
        private PageType _pageType = PageType.FixedSizeItem;
        private SizeClass _sizeClass = SizeClass.Class0;

        protected virtual PageType GetActualPageType()
        {
            return PageType.FixedSizeItem;
        }

        protected void CheckSizeClass(int pageSize)
        {
            if (DbItem.GetMaxSize(SizeClass) > pageSize - Length - 4)
                throw new PageFormatException("Unable to format page. The class size " + Enum.GetName(typeof(SizeClass), SizeClass) + " is too large for page " + pageSize + "bytes length.");
        }

        public static SizeClass GetSizeClass(IPage page)
        {
            byte scByte = page.Content[OnPageOffsets.SizeClass];
            return (SizeClass)scByte;
        }

        public static PageType GetPageType(IPage page)
        {
            byte ptByte = page.Content[OnPageOffsets.PageType];
            return (PageType)ptByte;
        }

        public static short GetHeaderLength(IPage page)
        {
            return BitConverter.ToInt16(page.Content, OnPageOffsets.HeaderLength);
        }

        public virtual SizeClass SizeClass
        {
            get { return _sizeClass; }
            set { _sizeClass = value; }
        }

        public PageType PageType
        {
            get { return _pageType; }
            protected set { _pageType = value; }
        }

        public short Length
        {
            get { return _length; }
            protected set { _length = value; }
        }

        public virtual void Read(IPage page)
        {
            Length = BitConverter.ToInt16(page.Content, OnPageOffsets.HeaderLength);
            PageType = (PageType)page.Content[OnPageOffsets.PageType];
            SizeClass = (SizeClass)page.Content[OnPageOffsets.SizeClass];
        }

        public virtual void WriteToPage(IPage page)
        {
            PageType = GetActualPageType();

            // write length of the header
            byte[] hlBytes = BitConverter.GetBytes(Length);
            hlBytes.CopyTo(page.Content, OnPageOffsets.HeaderLength);

            // write type of page
            page.Content[OnPageOffsets.PageType] = (byte)PageType;

            // write size class
            page.Content[OnPageOffsets.SizeClass] = (byte)SizeClass;
        }
    }
}